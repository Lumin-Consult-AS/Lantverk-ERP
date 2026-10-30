using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Erp.Core.Api.Auth;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Erp.Core.Tests;

[Collection(PostgresDatabaseCollection.Name)]
public sealed class AuthAndLoginTests(PostgresDatabaseFixture fixture)
{
    private const string TestOidcSigningKey = "test-only-external-oidc-signing-key-at-least-32-bytes!";
    private const string TestOidcIssuer = "https://oidc.test.local/realms/erp-dev";
    private const string TestOidcAudience = "erp-web";

    [Fact]
    public async Task OidcTokenExchange_AndEmptySystemSession_WorksEndToEnd()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // 1. Simulate external OIDC login token from Keycloak for Demo User 1
        var externalOidcJwt = CreateExternalOidcToken(DemoTenantSeeder.DemoUserSubject, "demo@demohandel.no");

        using var exchangeRequest = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/token");
        exchangeRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", externalOidcJwt);
        exchangeRequest.Content = JsonContent.Create(new TokenExchangeRequest());

        using var exchangeResponse = await client.SendAsync(exchangeRequest);
        Assert.Equal(HttpStatusCode.OK, exchangeResponse.StatusCode);

        var exchanged = await exchangeResponse.Content.ReadFromJsonAsync<TokenExchangeResponse>();
        Assert.NotNull(exchanged);
        Assert.Equal(DemoTenantSeeder.DemoTenantId, exchanged.TenantId);
        Assert.Equal("Demo Handel", exchanged.TenantName);
        Assert.Equal(DemoTenantSeeder.DemoCompanyId, exchanged.CompanyId);
        Assert.Equal("Demo Handel Norge AS", exchanged.CompanyName);
        Assert.Equal(DemoTenantSeeder.DemoUserId, exchanged.UserId);
        Assert.Contains("tenant.admin", exchanged.Roles);
        Assert.False(string.IsNullOrWhiteSpace(exchanged.AccessToken));

        // 2. Verify claims inside the issued internal short-lived JWT
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(exchanged.AccessToken);
        Assert.Equal("internal", jwt.Claims.First(c => c.Type == "token_use").Value);
        Assert.Equal(DemoTenantSeeder.DemoTenantId.ToString("D"), jwt.Claims.First(c => c.Type == "tenant_id").Value);
        Assert.Equal(DemoTenantSeeder.DemoCompanyId.ToString("D"), jwt.Claims.First(c => c.Type == "company_id").Value);
        Assert.Equal(DemoTenantSeeder.DemoUserId.ToString("D"), jwt.Subject);

        // 3. Call /api/v1/session using the internal token and verify the empty system state
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", exchanged.AccessToken);
        using var sessionResponse = await client.GetAsync("/api/v1/session");
        Assert.Equal(HttpStatusCode.OK, sessionResponse.StatusCode);

        using var sessionDoc = JsonDocument.Parse(await sessionResponse.Content.ReadAsStringAsync());
        var root = sessionDoc.RootElement;
        Assert.Equal(DemoTenantSeeder.DemoTenantId, root.GetProperty("tenant").GetProperty("id").GetGuid());
        Assert.Equal("Demo Handel Norge AS", root.GetProperty("company").GetProperty("name").GetString());
        Assert.Equal(0, root.GetProperty("installedApps").GetArrayLength());
        Assert.Equal(0, root.GetProperty("navigation").GetArrayLength());
    }

    [Fact]
    public async Task ExternalOidcToken_CannotAccessInternalEndpointsDirectly()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var externalOidcJwt = CreateExternalOidcToken(DemoTenantSeeder.DemoUserSubject, "demo@demohandel.no");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", externalOidcJwt);

        using var sessionResponse = await client.GetAsync("/api/v1/session");
        Assert.Equal(HttpStatusCode.Unauthorized, sessionResponse.StatusCode);

        using var companiesResponse = await client.GetAsync("/api/v1/core/companies");
        Assert.Equal(HttpStatusCode.Unauthorized, companiesResponse.StatusCode);
    }

    [Fact]
    public async Task ApiEndpointsWithoutWhereFilter_EnforceTenantIsolationPerLoggedInUser()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var tokenTenant1 = await ExchangeForInternalTokenAsync(client, DemoTenantSeeder.DemoUserSubject, "demo@demohandel.no");
        var tokenTenant2 = await ExchangeForInternalTokenAsync(client, DemoTenantSeeder.SecondUserSubject, "anna@nordiskimport.no");

        // Tenant 1 request
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenTenant1);
        using var compRes1 = await client.GetAsync("/api/v1/core/companies");
        Assert.Equal(HttpStatusCode.OK, compRes1.StatusCode);
        using var doc1 = JsonDocument.Parse(await compRes1.Content.ReadAsStringAsync());
        Assert.Single(doc1.RootElement.EnumerateArray());
        Assert.Equal("Demo Handel Norge AS", doc1.RootElement[0].GetProperty("name").GetString());

        // Tenant 2 request
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenTenant2);
        using var compRes2 = await client.GetAsync("/api/v1/core/companies");
        Assert.Equal(HttpStatusCode.OK, compRes2.StatusCode);
        using var doc2 = JsonDocument.Parse(await compRes2.Content.ReadAsStringAsync());
        Assert.Single(doc2.RootElement.EnumerateArray());
        Assert.Equal("Nordisk Import AS", doc2.RootElement[0].GetProperty("name").GetString());
    }

    private WebApplicationFactory<Program> CreateFactory()
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:MigrationConnection"] = fixture.MigrationConnectionString,
                    ["ConnectionStrings:ApplicationConnection"] = fixture.ApplicationConnectionString,
                    ["Database:AutoMigrateAndSeed"] = "false",
                    ["Auth:OidcTestSigningKey"] = TestOidcSigningKey,
                    ["Auth:OidcTestIssuer"] = TestOidcIssuer,
                    ["Auth:OidcAudience"] = TestOidcAudience
                });
            });
        });
    }

    private static async Task<string> ExchangeForInternalTokenAsync(HttpClient client, string subject, string email)
    {
        var externalJwt = CreateExternalOidcToken(subject, email);
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/token");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", externalJwt);
        req.Content = JsonContent.Create(new TokenExchangeRequest());

        using var res = await client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<TokenExchangeResponse>();
        return body!.AccessToken;
    }

    private static string CreateExternalOidcToken(string subject, string email)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TestOidcSigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var jwt = new JwtSecurityToken(
            issuer: TestOidcIssuer,
            audience: TestOidcAudience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, subject),
                new Claim(JwtRegisteredClaimNames.Email, email)
            ],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }
}

