using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Erp.Core.Tests;

/// <summary>User administration: add users, give roles, deactivate, and never lock the tenant out.</summary>
[Collection(PostgresDatabaseCollection.Name)]
public sealed class UserAdministrationTests(PostgresDatabaseFixture fixture) : IAsyncLifetime
{
    private const string TestOidcSigningKey = "test-only-external-oidc-signing-key-at-least-32-bytes!";
    private const string TestOidcIssuer = "https://oidc.test.local/realms/erp-dev";
    private const string TestOidcAudience = "erp-web";

    private readonly List<string> _subjects = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_subjects.Count == 0)
        {
            return;
        }

        await using var migrator = new NpgsqlConnection(fixture.MigrationConnectionString);
        await migrator.OpenAsync();
        await using var cmd = new NpgsqlCommand("""DELETE FROM core."user" WHERE external_subject = ANY(@subjects);""", migrator);
        cmd.Parameters.AddWithValue("subjects", _subjects.ToArray());
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task AnAdministrator_AddsUsersAndGivesRoles_AndOthersCannot()
    {
        await using var factory = CreateFactory();
        using var admin = await LoginAsync(factory, null);

        var subject = $"ola-{Guid.NewGuid():N}"[..16];
        _subjects.Add(subject);

        // Required fields
        using (var empty = await admin.PostAsJsonAsync("/api/v1/users", new { email = "x@y.no" }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        }

        // Add a user with a role; the base role is always included
        var created = await PostJsonAsync(admin, "/api/v1/users", new
        {
            externalSubject = subject,
            email = $"{subject}@demohandel.no",
            displayName = "Ola Nordmann",
            roles = new[] { "demo.manager" }
        }, HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        var roles = created.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ToList();
        Assert.Contains("demo.manager", roles);
        Assert.Contains("core.user", roles);
        Assert.True(created.GetProperty("isActive").GetBoolean());

        // The same subject cannot be added twice
        using (var duplicate = await admin.PostAsJsonAsync("/api/v1/users", new { externalSubject = subject, email = "a@b.no", displayName = "Dobbel" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        }

        // He is not an administrator, so he cannot manage users
        using var ola = await LoginAsync(factory, subject);
        Assert.False((await ola.GetFromJsonAsync<JsonElement>("/api/v1/permissions/me")).GetProperty("isAdmin").GetBoolean());
        using (var forbidden = await ola.GetAsync("/api/v1/users"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using (var forbiddenCreate = await ola.PostAsJsonAsync("/api/v1/users", new { externalSubject = "x", email = "x@y.no", displayName = "X" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbiddenCreate.StatusCode);
        }

        // Promoted to administrator: effective for the next token
        using (var promote = await admin.PutAsJsonAsync($"/api/v1/users/{id}/roles", new { roles = new[] { "tenant.admin" } }))
        {
            Assert.Equal(HttpStatusCode.OK, promote.StatusCode);
        }

        using var olaAgain = await LoginAsync(factory, subject);
        Assert.True((await olaAgain.GetFromJsonAsync<JsonElement>("/api/v1/permissions/me")).GetProperty("isAdmin").GetBoolean());

        // Deactivated users cannot log in; reactivated ones can again
        using (var deactivate = await admin.PostAsync($"/api/v1/users/{id}/deactivate", null))
        {
            Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        }

        using (var blocked = await new HttpClient(factory.Server.CreateHandler()) { BaseAddress = factory.Server.BaseAddress }
                   .PostAsJsonAsync("/api/v1/auth/dev-login", new { subject }))
        {
            Assert.NotEqual(HttpStatusCode.OK, blocked.StatusCode);
        }

        using (var activate = await admin.PostAsync($"/api/v1/users/{id}/activate", null))
        {
            Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        }

        using var back = await LoginAsync(factory, subject);
        Assert.True(back.DefaultRequestHeaders.Authorization is not null);

        // The list shows him
        var all = await admin.GetFromJsonAsync<JsonElement>("/api/v1/users");
        Assert.Contains(all.EnumerateArray(), u => u.GetProperty("externalSubject").GetString() == subject);
    }

    [Fact]
    public async Task TheLastAdministrator_CannotBeDemotedOrDeactivated()
    {
        await using var factory = CreateFactory();

        // The second tenant has exactly one administrator (the seeded user): the guard stops every way of locking it out
        using var admin = await LoginAsync(factory, DemoTenantSeeder.SecondUserSubject);
        var users = await admin.GetFromJsonAsync<JsonElement>("/api/v1/users");
        var self = users.EnumerateArray().Single(u => u.GetProperty("externalSubject").GetString() == DemoTenantSeeder.SecondUserSubject);
        var id = self.GetProperty("id").GetGuid();

        using (var demote = await admin.PutAsJsonAsync($"/api/v1/users/{id}/roles", new { roles = new[] { "core.user" } }))
        {
            Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
        }

        using (var deactivate = await admin.PostAsync($"/api/v1/users/{id}/deactivate", null))
        {
            Assert.Equal(HttpStatusCode.Conflict, deactivate.StatusCode);
        }

        // Another tenant's users are not visible, whatever the caller asks
        Assert.DoesNotContain(users.EnumerateArray(), u => u.GetProperty("externalSubject").GetString() == DemoTenantSeeder.DemoUserSubject);
    }

    private static async Task<JsonElement> PostJsonAsync(HttpClient client, string url, object body, HttpStatusCode expected)
    {
        using var res = await client.PostAsJsonAsync(url, body);
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == expected, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> factory, string? subject)
    {
        var client = factory.CreateClient();
        using var res = await client.PostAsJsonAsync("/api/v1/auth/dev-login", new { subject });
        res.EnsureSuccessStatusCode();
        var token = JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
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
                    ["Auth:EnableDevLogin"] = "true",
                    ["Auth:OidcTestSigningKey"] = TestOidcSigningKey,
                    ["Auth:OidcTestIssuer"] = TestOidcIssuer,
                    ["Auth:OidcAudience"] = TestOidcAudience
                });
            });
        });
    }
}
