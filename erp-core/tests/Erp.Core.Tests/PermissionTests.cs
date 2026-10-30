using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.Client.Models;
using Erp.Client.Services;
using Erp.Core.Domain.Entities;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Persistence;
using Erp.Sdk.Contracts.V1;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Erp.Core.Tests;

[Collection(PostgresDatabaseCollection.Name)]
public sealed class PermissionTests(PostgresDatabaseFixture fixture) : IAsyncLifetime
{
    private readonly List<string> _createdSubjects = [];

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Other tests expect the demo tenant to have exactly its seeded user, so remove the users created here.</summary>
    public async Task DisposeAsync()
    {
        if (_createdSubjects.Count == 0)
        {
            return;
        }

        await using var migrator = new NpgsqlConnection(fixture.MigrationConnectionString);
        await migrator.OpenAsync();
        await using var cmd = new NpgsqlCommand("""DELETE FROM core."user" WHERE external_subject = ANY(@subjects);""", migrator);
        cmd.Parameters.AddWithValue("subjects", _createdSubjects.ToArray());
        await cmd.ExecuteNonQueryAsync();
    }

    private const string TestOidcSigningKey = "test-only-external-oidc-signing-key-at-least-32-bytes!";
    private const string TestOidcIssuer = "https://oidc.test.local/realms/erp-dev";
    private const string TestOidcAudience = "erp-web";

    private sealed class Session(HttpClient http, ErpApiClient api)
    {
        public HttpClient Http { get; } = http;
        public ErpApiClient Api { get; } = api;
    }

    private static Dictionary<string, object?> Party(string number) => new()
    {
        ["partyNumber"] = number,
        ["name"] = "Tillatelse AS",
        ["partyType"] = "organization",
        ["role"] = "customer",
        ["organizationNumber"] = "987001002"
    };

    [Fact]
    public async Task UsersWithoutAMappedRole_AreDenied_AndAdminsAreNot()
    {
        await using var factory = CreateFactory();
        var nobody = await CreateUserAsync("nobody", $"demo.nobody-{Guid.NewGuid():N}");
        var admin = await LoginAsync(factory, null);
        var other = await LoginAsync(factory, nobody);
        Assert.True(await admin.Api.InstallBuiltInAppAsync("core.parties"));

        try
        {
            var party = await admin.Api.CreateRecordAsync("core.party", Party("P-PERM-1"));

            using var read = await other.Http.GetAsync($"/api/v1/{DemoTenantSeeder.DemoCompanyId}/core.party/{party.Id}");
            Assert.Equal(HttpStatusCode.Forbidden, read.StatusCode);
            using var query = await other.Http.GetAsync($"/api/v1/{DemoTenantSeeder.DemoCompanyId}/core.party");
            Assert.Equal(HttpStatusCode.Forbidden, query.StatusCode);
            using var create = await other.Http.PostAsJsonAsync($"/api/v1/{DemoTenantSeeder.DemoCompanyId}/core.party", Party("P-PERM-2"));
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
            Assert.Contains("core.party", await create.Content.ReadAsStringAsync());

            Assert.Equal(party.Id, (await admin.Api.GetRecordAsync("core.party", party.Id))!.Id);
        }
        finally
        {
            await admin.Api.UninstallAppAsync("core.parties");
        }
    }

    [Fact]
    public async Task RolesAreComposedOfPermissionSets_AndEveryOperationChecksItsOwnAction()
    {
        await using var factory = CreateFactory();
        var readerRole = $"demo.reader-{Guid.NewGuid():N}";
        var reader = await CreateUserAsync("reader", readerRole);
        var admin = await LoginAsync(factory, null);
        var session = await LoginAsync(factory, reader);
        Assert.True(await admin.Api.InstallBuiltInAppAsync("core.parties"));

        try
        {
            var party = await admin.Api.CreateRecordAsync("core.party", Party("P-PERM-3"));
            var path = $"/api/v1/{DemoTenantSeeder.DemoCompanyId}/core.party";

            using var before = await session.Http.GetAsync($"{path}/{party.Id}");
            Assert.Equal(HttpStatusCode.Forbidden, before.StatusCode);

            // Read-only permission set: may read and query, but not change anything
            await SetRoleAsync(admin, readerRole, "core.parties.reader");
            using var read = await session.Http.GetAsync($"{path}/{party.Id}");
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            using var query = await session.Http.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, query.StatusCode);

            using var create = await session.Http.PostAsJsonAsync(path, Party("P-PERM-4"));
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);

            using var update = new HttpRequestMessage(HttpMethod.Patch, $"{path}/{party.Id}")
            {
                Content = JsonContent.Create(new Dictionary<string, object?> { ["name"] = "Endret" })
            };
            update.Headers.IfMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue($"\"{party.RowVersion}\""));
            using var updated = await session.Http.SendAsync(update);
            Assert.Equal(HttpStatusCode.Forbidden, updated.StatusCode);

            using var transition = new HttpRequestMessage(HttpMethod.Post, $"{path}/{party.Id}/transitions/deactivate");
            transition.Headers.IfMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue($"\"{party.RowVersion}\""));
            using var transitioned = await session.Http.SendAsync(transition);
            Assert.Equal(HttpStatusCode.Forbidden, transitioned.StatusCode); // the transition requires 'update'

            using var delete = new HttpRequestMessage(HttpMethod.Delete, $"{path}/{party.Id}");
            delete.Headers.IfMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue($"\"{party.RowVersion}\""));
            using var deleted = await session.Http.SendAsync(delete);
            Assert.Equal(HttpStatusCode.Forbidden, deleted.StatusCode);

            using var me = JsonDocument.Parse(await session.Http.GetStringAsync("/api/v1/permissions/me"));
            Assert.False(me.RootElement.GetProperty("isAdmin").GetBoolean());
            var grants = me.RootElement.GetProperty("grants").EnumerateArray()
                .Single(g => g.GetProperty("objectId").GetString() == "core.party");
            Assert.Equal(["read"], grants.GetProperty("actions").EnumerateArray().Select(a => a.GetString()).ToArray());

            // The full permission set lets the same user work with the records
            await SetRoleAsync(admin, readerRole, "core.parties.manager");
            using var created = await session.Http.PostAsJsonAsync(path, Party("P-PERM-5"));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            using var transitionAgain = new HttpRequestMessage(HttpMethod.Post, $"{path}/{party.Id}/transitions/deactivate");
            transitionAgain.Headers.IfMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue($"\"{party.RowVersion}\""));
            using var ok = await session.Http.SendAsync(transitionAgain);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

            // Removing the permission sets from the role takes the access away again
            await SetRoleAsync(admin, readerRole);
            using var after = await session.Http.GetAsync($"{path}/{party.Id}");
            Assert.Equal(HttpStatusCode.Forbidden, after.StatusCode);
        }
        finally
        {
            await admin.Api.UninstallAppAsync("core.parties");
        }
    }

    [Fact]
    public async Task RoleAdministration_IsRestrictedToTenantAdmins_AndValidatesInput()
    {
        await using var factory = CreateFactory();
        var role = $"demo.clerk-{Guid.NewGuid():N}";
        var clerk = await CreateUserAsync("clerk", role);
        var admin = await LoginAsync(factory, null);
        var session = await LoginAsync(factory, clerk);
        Assert.True(await admin.Api.InstallBuiltInAppAsync("core.parties"));

        try
        {
            using var forbidden = await session.Http.PutAsJsonAsync(
                $"/api/v1/roles/{role}/permission-sets", new { permissionSets = new[] { "core.parties.manager" } });
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

            using var unknown = await admin.Http.PutAsJsonAsync(
                $"/api/v1/roles/{role}/permission-sets", new { permissionSets = new[] { "no.such.set" } });
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

            using var adminRole = await admin.Http.PutAsJsonAsync(
                "/api/v1/roles/tenant.admin/permission-sets", new { permissionSets = Array.Empty<string>() });
            Assert.Equal(HttpStatusCode.BadRequest, adminRole.StatusCode);

            await SetRoleAsync(admin, role, "core.parties.reader");
            using var roles = JsonDocument.Parse(await admin.Http.GetStringAsync("/api/v1/roles"));
            var entry = roles.RootElement.EnumerateArray().Single(r => r.GetProperty("role").GetString() == role);
            Assert.Equal(["core.parties.reader"], entry.GetProperty("permissionSets").EnumerateArray().Select(s => s.GetString()).ToArray());

            // The change is in the audit trail
            await using var migrator = new NpgsqlConnection(fixture.MigrationConnectionString);
            await migrator.OpenAsync();
            await using var audit = new NpgsqlCommand(
                "SELECT count(*) FROM core.role_permission_set_audit WHERE operation = 'INSERT' AND new_data ->> 'role' = @role;", migrator);
            audit.Parameters.AddWithValue("role", role);
            Assert.Equal(1L, await audit.ExecuteScalarAsync());
        }
        finally
        {
            await admin.Api.UninstallAppAsync("core.parties");
        }
    }

    [Fact]
    public async Task CallbacksFromApps_AreLimitedToTheRolesOfTheCallingUser()
    {
        await using var factory = CreateFactory();
        var role = $"demo.grpc-{Guid.NewGuid():N}";
        var admin = await LoginAsync(factory, null);
        Assert.True(await admin.Api.InstallBuiltInAppAsync("core.parties"));

        try
        {
            using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = factory.Server.CreateHandler() });
            var client = new DataService.DataServiceClient(channel);

            string TokenFor(params string[] roles)
            {
                using var scope = factory.Services.CreateScope();
                return scope.ServiceProvider.GetRequiredService<IInternalTokenIssuer>().IssueShortLivedCallToken(
                    DemoTenantSeeder.DemoTenantId, DemoTenantSeeder.DemoUserId, DemoTenantSeeder.DemoCompanyId, roles);
            }

            DataQueryRequest Request(string token) => new()
            {
                Context = new CallContextProto { TenantId = DemoTenantSeeder.DemoTenantId.ToString("D"), AccessToken = token },
                ObjectId = "core.party"
            };

            var denied = await Assert.ThrowsAsync<RpcException>(async () => await client.QueryAsync(Request(TokenFor(role))));
            Assert.Equal(StatusCode.PermissionDenied, denied.StatusCode);

            await SetRoleAsync(admin, role, "core.parties.reader");
            Assert.NotNull(await client.QueryAsync(Request(TokenFor(role))));
            Assert.NotNull(await client.QueryAsync(Request(TokenFor("tenant.admin"))));
        }
        finally
        {
            await admin.Api.UninstallAppAsync("core.parties");
        }
    }

    [Fact]
    public async Task StartingAWorkflow_RequiresThePermissionOfTheTransition_AndAttentionFollowsReadAccess()
    {
        await using var factory = CreateFactory();
        var viewerRole = $"demo.viewer-{Guid.NewGuid():N}";
        var userRole = $"demo.user-{Guid.NewGuid():N}";
        var strangerRole = $"demo.stranger-{Guid.NewGuid():N}";
        var viewer = await CreateUserAsync("viewer", viewerRole);
        var user = await CreateUserAsync("user", userRole);
        var stranger = await CreateUserAsync("stranger", strangerRole);

        var admin = await LoginAsync(factory, null);
        var viewerSession = await LoginAsync(factory, viewer);
        var userSession = await LoginAsync(factory, user);
        var strangerSession = await LoginAsync(factory, stranger);
        Assert.True(await admin.Api.InstallBuiltInAppAsync("demo.approvals"));

        try
        {
            await SetRoleAsync(admin, viewerRole, "demo.approvals.viewer");
            await SetRoleAsync(admin, userRole, "demo.approvals.user");

            var expense = await admin.Api.CreateRecordAsync(
                "demo.expense", new Dictionary<string, object?> { ["title"] = "Tillatelsestest", ["amount"] = 500m });

            // The viewer may read the expense but not submit it (submit requires 'update')
            Assert.NotNull(await viewerSession.Api.GetRecordAsync("demo.expense", expense.Id));
            using var submit = new HttpRequestMessage(
                HttpMethod.Post, $"/api/v1/{DemoTenantSeeder.DemoCompanyId}/demo.expense/{expense.Id}/transitions/submit");
            submit.Headers.IfMatch.Add(new System.Net.Http.Headers.EntityTagHeaderValue($"\"{expense.RowVersion}\""));
            using var refused = await viewerSession.Http.SendAsync(submit);
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

            // A user with the full set can submit, and the workflow starts
            var submitted = await userSession.Api.ExecuteTransitionAsync("demo.expense", expense.Id, "submit", expense.RowVersion);
            Assert.Equal("pending", I18nService.UnwrapDisplayString(submitted.Fields["status"]));

            // Attention items with a permission audience follow read access
            await admin.Api.CreateRecordAsync("demo.expense", new Dictionary<string, object?> { ["title"] = "Kladd", ["amount"] = 1m });
            Assert.NotNull(await AttentionAsync(viewerSession, "demo.approvals.drafts"));
            Assert.NotNull(await AttentionAsync(admin, "demo.approvals.drafts"));
            Assert.Null(await AttentionAsync(strangerSession, "demo.approvals.drafts"));
        }
        finally
        {
            await admin.Api.UninstallAppAsync("demo.approvals");
            await ResetStateAsync();
        }
    }

    // ------------------------------------------------------------------ helpers

    private static async Task<JsonElement?> AttentionAsync(Session session, string id)
    {
        using var doc = JsonDocument.Parse(await session.Http.GetStringAsync("/api/v1/attention"));
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.GetProperty("id").GetString() == id)
            {
                return item.Clone();
            }
        }

        return null;
    }

    private static async Task SetRoleAsync(Session admin, string role, params string[] permissionSets)
    {
        using var res = await admin.Http.PutAsJsonAsync($"/api/v1/roles/{role}/permission-sets", new { permissionSets });
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
    }

    private static async Task<Session> LoginAsync(WebApplicationFactory<Program> factory, string? subject)
    {
        var http = factory.CreateClient();
        var api = new ErpApiClient(http, new I18nService());
        Assert.NotNull(await api.DevLoginAsync(subject));
        return new Session(http, api);
    }

    /// <summary>Creates a user holding the given role and returns the subject to log in with.</summary>
    private async Task<string> CreateUserAsync(string name, string role)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var subject = $"{name}-{unique}";

        var tenantContext = new TenantContext();
        tenantContext.Set(DemoTenantSeeder.DemoTenantId, DemoTenantSeeder.DemoUserId);
        await using var db = fixture.CreateAppDbContext(tenantContext);
        db.Users.Add(new User
        {
            Id = Guid.NewGuid(),
            TenantId = DemoTenantSeeder.DemoTenantId,
            CompanyId = DemoTenantSeeder.DemoCompanyId,
            ExternalSubject = subject,
            Email = $"{subject}@demohandel.no",
            DisplayName = name,
            Roles = [role, "core.user"],
            AccessibleCompanyIds = [DemoTenantSeeder.DemoCompanyId],
            IsActive = true
        });
        await db.SaveChangesAsync();
        _createdSubjects.Add(subject);
        return subject;
    }

    private async Task ResetStateAsync()
    {
        await using var migrator = new NpgsqlConnection(fixture.MigrationConnectionString);
        await migrator.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE core.task SET status = 'cancelled' WHERE status IN ('open', 'waiting');
            UPDATE core.workflow_instance SET status = 'rejected', completed_at = now() WHERE status = 'running';
            """,
            migrator);
        await cmd.ExecuteNonQueryAsync();
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
                    ["Auth:OidcAudience"] = TestOidcAudience,
                    ["Email:SmtpHost"] = ""
                });
            });
        });
    }
}
