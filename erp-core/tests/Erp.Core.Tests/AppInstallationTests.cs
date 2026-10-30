using System.IdentityModel.Tokens.Jwt;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Erp.Core.Api.Auth;
using Erp.Core.Infrastructure.Apps;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Erp.Core.Tests;

[Collection(PostgresDatabaseCollection.Name)]
public sealed class AppInstallationTests(PostgresDatabaseFixture fixture)
{
    private const string TestOidcSigningKey = "test-only-external-oidc-signing-key-at-least-32-bytes!";
    private const string TestOidcIssuer = "https://oidc.test.local/realms/erp-dev";
    private const string TestOidcAudience = "erp-web";

    [Fact]
    public async Task CoreParties_InstallsFromSignedZip_EnforcesDeclarativeRules_CapturesSnapshots_AndUninstalls()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        // 1. Verify published JSON schema endpoint is served
        using var schemaRes = await client.GetAsync("/api/v1/schemas/app-manifest.json");
        Assert.Equal(HttpStatusCode.OK, schemaRes.StatusCode);
        var schemaText = await schemaRes.Content.ReadAsStringAsync();
        Assert.Contains("https://schemas.erp.sdk/v1/app-manifest.schema.json", schemaText);

        var tokenTenant1 = await ExchangeForInternalTokenAsync(client, DemoTenantSeeder.DemoUserSubject, "demo@demohandel.no");
        var tokenTenant2 = await ExchangeForInternalTokenAsync(client, DemoTenantSeeder.SecondUserSubject, "anna@nordiskimport.no");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenTenant1);
        var companyId = DemoTenantSeeder.DemoCompanyId;

        // 2. Pack erp-apps/core.parties into a signed zip package
        var corePartiesDir = ResolveCorePartiesDirectory();
        var signedZipBytes = AppPackageFormat.CreateSignedZipFromDirectory(corePartiesDir);

        // 3. Install core.parties in Tenant 1
        using var installContent = new ByteArrayContent(signedZipBytes);
        installContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        using var installRes = await client.PostAsync("/api/v1/apps/install", installContent);
        Assert.Equal(HttpStatusCode.OK, installRes.StatusCode);

        try
        {
            // 4. Verify core.parties is listed in installed apps, pages, and permission sets for Tenant 1
            using var appsRes = await client.GetAsync("/api/v1/apps");
            appsRes.EnsureSuccessStatusCode();
            using var appsDoc = JsonDocument.Parse(await appsRes.Content.ReadAsStringAsync());
            var installedApp = Assert.Single(appsDoc.RootElement.EnumerateArray());
            Assert.Equal("core.parties", installedApp.GetProperty("appId").GetString());
            Assert.Equal("1.0.0", installedApp.GetProperty("version").GetString());
            Assert.True(installedApp.GetProperty("isDeclarative").GetBoolean());

            using var pagesRes = await client.GetAsync("/api/v1/metadata/pages");
            pagesRes.EnsureSuccessStatusCode();
            using var pagesDoc = JsonDocument.Parse(await pagesRes.Content.ReadAsStringAsync());
            var pageIds = pagesDoc.RootElement.EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToList();
            Assert.Contains("core.party-list", pageIds);
            Assert.Contains("core.party-card", pageIds);

            using var permsRes = await client.GetAsync("/api/v1/metadata/permissions");
            permsRes.EnsureSuccessStatusCode();
            using var permsDoc = JsonDocument.Parse(await permsRes.Content.ReadAsStringAsync());
            var permIds = permsDoc.RootElement.EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToList();
            Assert.Contains("core.parties.reader", permIds);
            Assert.Contains("core.parties.manager", permIds);

            // 5. Verify declarative requiredWhen & pattern rules on core.party via REST Data API
            // 5a. Organization party WITHOUT organizationNumber must be rejected (400 Bad Request)
            using var missingOrgNoRes = await client.PostAsJsonAsync(
                $"/api/v1/{companyId}/core.party",
                new Dictionary<string, object?>
                {
                    ["partyNumber"] = "K-1001",
                    ["name"] = "Mangler OrgNr AS",
                    ["partyType"] = "organization",
                    ["role"] = "customer"
                });
            Assert.Equal(HttpStatusCode.BadRequest, missingOrgNoRes.StatusCode);

            // 5b. Organization party with invalid organizationNumber pattern (not 9 digits) must be rejected
            using var invalidOrgNoRes = await client.PostAsJsonAsync(
                $"/api/v1/{companyId}/core.party",
                new Dictionary<string, object?>
                {
                    ["partyNumber"] = "K-1002",
                    ["name"] = "Ugyldig OrgNr AS",
                    ["partyType"] = "organization",
                    ["role"] = "customer",
                    ["organizationNumber"] = "12345"
                });
            Assert.Equal(HttpStatusCode.BadRequest, invalidOrgNoRes.StatusCode);

            // 5c. Organization party WITH valid 9-digit organizationNumber succeeds (201 Created)
            using var validOrgRes = await client.PostAsJsonAsync(
                $"/api/v1/{companyId}/core.party",
                new Dictionary<string, object?>
                {
                    ["partyNumber"] = "K-1003",
                    ["name"] = "Byggmester Bob AS",
                    ["partyType"] = "organization",
                    ["role"] = "customer",
                    ["organizationNumber"] = "987654321",
                    ["email"] = "post@byggmesterbob.no"
                });
            Assert.Equal(HttpStatusCode.Created, validOrgRes.StatusCode);
            using var createdOrgDoc = JsonDocument.Parse(await validOrgRes.Content.ReadAsStringAsync());
            var orgPartyId = createdOrgDoc.RootElement.GetProperty("id").GetGuid();

            // 5d. Person party WITHOUT organizationNumber succeeds because requiredWhen(partyType == organization) is false
            using var validPersonRes = await client.PostAsJsonAsync(
                $"/api/v1/{companyId}/core.party",
                new Dictionary<string, object?>
                {
                    ["partyNumber"] = "K-1004",
                    ["name"] = "Kari Nordmann",
                    ["partyType"] = "person",
                    ["role"] = "customer",
                    ["email"] = "kari@example.no"
                });
            Assert.Equal(HttpStatusCode.Created, validPersonRes.StatusCode);

            // 5e. Create address and contact person linked to the organization party
            using var addrRes = await client.PostAsJsonAsync(
                $"/api/v1/{companyId}/core.partyaddress",
                new Dictionary<string, object?>
                {
                    ["party"] = orgPartyId,
                    ["addressType"] = "delivery",
                    ["streetLine1"] = "Storgata 12",
                    ["postalCode"] = "0155",
                    ["city"] = "Oslo",
                    ["countryCode"] = "NO",
                    ["isPrimary"] = true
                });
            Assert.Equal(HttpStatusCode.Created, addrRes.StatusCode);

            using var contactRes = await client.PostAsJsonAsync(
                $"/api/v1/{companyId}/core.partycontact",
                new Dictionary<string, object?>
                {
                    ["party"] = orgPartyId,
                    ["fullName"] = "Bob Byggmester",
                    ["roleTitle"] = "Daglig leder",
                    ["email"] = "bob@byggmesterbob.no",
                    ["phone"] = "+47 900 00 000",
                    ["isPrimary"] = true
                });
            Assert.Equal(HttpStatusCode.Created, contactRes.StatusCode);

            // 6. Verify Tenant 2 isolation: Tenant 2 has no installed apps and no snapshots from Tenant 1
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenTenant2);
            using var tenant2AppsRes = await client.GetAsync("/api/v1/apps");
            tenant2AppsRes.EnsureSuccessStatusCode();
            using var tenant2AppsDoc = JsonDocument.Parse(await tenant2AppsRes.Content.ReadAsStringAsync());
            Assert.Empty(tenant2AppsDoc.RootElement.EnumerateArray());
        }
        finally
        {
            // 7. Uninstall core.parties in Tenant 1
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenTenant1);
            using var uninstallRes = await client.DeleteAsync("/api/v1/apps/core.parties");
            Assert.Equal(HttpStatusCode.OK, uninstallRes.StatusCode);
        }

        // 8. Verify core.parties is uninstalled and snapshots recorded both install and uninstall
        using var afterAppsRes = await client.GetAsync("/api/v1/apps");
        afterAppsRes.EnsureSuccessStatusCode();
        using var afterAppsDoc = JsonDocument.Parse(await afterAppsRes.Content.ReadAsStringAsync());
        Assert.Empty(afterAppsDoc.RootElement.EnumerateArray());

        using var snapshotsRes = await client.GetAsync("/api/v1/metadata/snapshots");
        snapshotsRes.EnsureSuccessStatusCode();
        using var snapshotsDoc = JsonDocument.Parse(await snapshotsRes.Content.ReadAsStringAsync());
        var snapshots = snapshotsDoc.RootElement.EnumerateArray().ToList();
        Assert.True(snapshots.Count >= 2);
        Assert.Contains(snapshots, s =>
            s.GetProperty("triggerAction").GetString() == "install"
            && s.GetProperty("triggerAppId").GetString() == "core.parties");
        Assert.Contains(snapshots, s =>
            s.GetProperty("triggerAction").GetString() == "uninstall"
            && s.GetProperty("triggerAppId").GetString() == "core.parties");
    }

    [Fact]
    public async Task InstallPackage_EnforcesSignatureAndDependencies_AndPreventsUninstallingDependedUponApp()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var tokenTenant1 = await ExchangeForInternalTokenAsync(client, DemoTenantSeeder.DemoUserSubject, "demo@demohandel.no");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenTenant1);

        // 1. Unsigned zip package must be rejected with 422 Unprocessable Entity
        using (var unsignedMs = new MemoryStream())
        {
            using (var archive = new ZipArchive(unsignedMs, ZipArchiveMode.Create, leaveOpen: true))
            {
                var entry = archive.CreateEntry("app.yaml");
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write("id: core.parties\nversion: 1.0.0\nname: Parter\n");
            }

            using var unsignedContent = new ByteArrayContent(unsignedMs.ToArray());
            unsignedContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            using var unsignedRes = await client.PostAsync("/api/v1/apps/install", unsignedContent);
            Assert.Equal(HttpStatusCode.UnprocessableEntity, unsignedRes.StatusCode);
        }

        // 2. Dependent app installed before core.parties must be rejected with 400 Bad Request
        var dependentPackage = AppPackageFormat.CreateSignedZipFromFiles(new Dictionary<string, string>
        {
            ["app.yaml"] = """
                id: demo.contacts
                version: 1.0.0
                name: Kontakter
                dependencies:
                  core.parties: ">=1.0.0 <2.0.0"
                objects:
                  demo.contacttag:
                    label: Kontakttagg
                    fields:
                      name: { type: string, required: true }
                """
        });

        using (var depContent = new ByteArrayContent(dependentPackage))
        {
            depContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            using var depFailRes = await client.PostAsync("/api/v1/apps/install", depContent);
            Assert.Equal(HttpStatusCode.BadRequest, depFailRes.StatusCode);
        }

        // 3. Install core.parties first, then demo.contacts
        var corePartiesZip = AppPackageFormat.CreateSignedZipFromDirectory(ResolveCorePartiesDirectory());
        using (var cpContent = new ByteArrayContent(corePartiesZip))
        {
            cpContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            using var cpRes = await client.PostAsync("/api/v1/apps/install", cpContent);
            Assert.Equal(HttpStatusCode.OK, cpRes.StatusCode);
        }

        try
        {
            using (var depContent2 = new ByteArrayContent(dependentPackage))
            {
                depContent2.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                using var depOkRes = await client.PostAsync("/api/v1/apps/install", depContent2);
                Assert.Equal(HttpStatusCode.OK, depOkRes.StatusCode);
            }

            // 4. Attempting to uninstall core.parties while demo.contacts is installed must return 409 Conflict
            using var blockedUninstallRes = await client.DeleteAsync("/api/v1/apps/core.parties");
            Assert.Equal(HttpStatusCode.Conflict, blockedUninstallRes.StatusCode);

            // 5. Uninstall demo.contacts first
            using var uninstallDepRes = await client.DeleteAsync("/api/v1/apps/demo.contacts");
            Assert.Equal(HttpStatusCode.OK, uninstallDepRes.StatusCode);
        }
        finally
        {
            // 6. Now uninstall core.parties cleanly
            using var uninstallCoreRes = await client.DeleteAsync("/api/v1/apps/core.parties");
            Assert.Equal(HttpStatusCode.OK, uninstallCoreRes.StatusCode);
        }
    }

    private static string ResolveCorePartiesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "erp-apps", "core.parties");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate 'erp-apps/core.parties' directory from test runner base directory.");
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

