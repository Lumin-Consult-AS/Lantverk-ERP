using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Erp.Core.Api.Auth;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Xunit;

namespace Erp.Core.Tests;

[Collection(PostgresDatabaseCollection.Name)]
public sealed class MetadataAndDataApiTests(PostgresDatabaseFixture fixture)
{
    private const string TestOidcSigningKey = "test-only-external-oidc-signing-key-at-least-32-bytes!";
    private const string TestOidcIssuer = "https://oidc.test.local/realms/erp-dev";
    private const string TestOidcAudience = "erp-web";

    private const string SampleManifestYaml = """
        id: demo.contacts
        version: 1.0.0

        enums:
          demo.channel: [email, phone, sms]

        objects:
          core.party:
            ownerApp: core.parties
            label: Part
            keys:
              - name: party_number
                fields: [partyNumber]
                scopeToCompany: true
            fields:
              partyNumber:      { type: string, maxLength: 50, required: true }
              name:             { type: string, maxLength: 200, required: true }
              description:      { type: text }
              rating:           { type: integer, default: 3 }
              discountPct:      { type: decimal, scale: 2, default: 0.00 }
              isCustomer:       { type: boolean, default: true }
              establishedDate:  { type: date }
              lastVerifiedAt:   { type: datetime }
              externalId:       { type: uuid }
              creditLimit:      { type: money }
              metadataJson:     { type: json }
              logoFileId:       { type: blob }

          demo.contactnote:
            ownerApp: demo.contacts
            label: Kontaktnotat
            fields:
              party:    { type: reference, to: core.party, required: true, onDelete: cascade }
              text:     { type: text, required: true }
              followUp: { type: date }

        extensions:
          core.party:
            fields:
              preferredChannel: { type: enum, of: demo.channel }

        interfaces:
          sales.poschannel:
            operations:
              - name: PushPrices
              - name: PullSales

        events:
          publishes:
            - demo.contactnote.created@1
        """;

    [Fact]
    public async Task YamlObject_ProvisionsPhysicalSchemaAndTable_AndSupportsFullRestCrudFilterAndBatch()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var token = await LoginAsync(client, DemoTenantSeeder.DemoUserSubject, "demo@demohandel.no");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 1. Register YAML metadata via REST
        using var yamlContent = new StringContent(SampleManifestYaml, Encoding.UTF8, "application/x-yaml");
        using var metaRes = await client.PostAsync("/api/v1/metadata/yaml", yamlContent);
        var metaBody = await metaRes.Content.ReadAsStringAsync();
        Assert.True(metaRes.StatusCode == HttpStatusCode.OK, $"Expected 200 OK, got {(int)metaRes.StatusCode}: {metaBody}");

        // 2. Verify physical PostgreSQL columns (including extension column and money columns)
        await using (var conn = new NpgsqlConnection(fixture.ApplicationConnectionString))
        {
            await conn.OpenAsync();
            await using var colCmd = new NpgsqlCommand(
                """
                SELECT column_name
                  FROM information_schema.columns
                 WHERE table_schema = 'core'
                   AND table_name = 'party';
                """,
                conn);

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await colCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                columns.Add(reader.GetString(0));
            }

            // Extension field from 'demo.contacts' on 'core.party' is stored as its own physical column
            Assert.Contains("demo_contacts_preferred_channel", columns);
            // Money field is stored as two physical columns
            Assert.Contains("credit_limit_amount", columns);
            Assert.Contains("credit_limit_currency", columns);
        }

        var companyId = DemoTenantSeeder.DemoCompanyId;

        // 3. Create a core.party record via REST POST
        var createPayload = new Dictionary<string, object?>
        {
            ["partyNumber"] = "P-1001",
            ["name"] = "Norsk Trevare AS",
            ["description"] = "Hovedleverandør av laft",
            ["rating"] = 5,
            ["discountPct"] = 12.50m,
            ["isCustomer"] = true,
            ["establishedDate"] = "2020-05-17",
            ["lastVerifiedAt"] = "2026-10-30T12:00:00Z",
            ["externalId"] = Guid.NewGuid(),
            ["creditLimit"] = new { amount = 250000.00m, currency = "NOK" },
            ["metadataJson"] = new { region = "Innlandet", tier = "gold" },
            ["logoFileId"] = Guid.NewGuid(),
            ["demo.contacts.preferredChannel"] = "email"
        };

        using var createRes = await client.PostAsJsonAsync($"/api/v1/{companyId}/core.party", createPayload);
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        Assert.Equal("\"1\"", createRes.Headers.ETag?.Tag);

        using var createdDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
        var partyId = createdDoc.RootElement.GetProperty("id").GetGuid();
        Assert.Equal(1L, createdDoc.RootElement.GetProperty("rowVersion").GetInt64());
        Assert.Equal("Norsk Trevare AS", createdDoc.RootElement.GetProperty("fields").GetProperty("name").GetString());
        Assert.Equal("email", createdDoc.RootElement.GetProperty("fields").GetProperty("demo.contacts.preferredChannel").GetString());
        Assert.Equal(250000.00m, createdDoc.RootElement.GetProperty("fields").GetProperty("creditLimit").GetProperty("amount").GetDecimal());

        // 4. Query via REST GET with OData-like filter tree
        using var queryRes = await client.GetAsync(
            $"/api/v1/{companyId}/core.party?filter=rating eq 5 and name contains 'Trevare'&select=partyNumber,name,creditLimit");
        Assert.Equal(HttpStatusCode.OK, queryRes.StatusCode);
        using var queryDoc = JsonDocument.Parse(await queryRes.Content.ReadAsStringAsync());
        var matched = Assert.Single(queryDoc.RootElement.EnumerateArray());
        Assert.Equal(partyId, matched.GetProperty("id").GetGuid());

        // 5. Update via REST PATCH with If-Match optimistic concurrency
        using var patchReq = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/{companyId}/core.party/{partyId}");
        patchReq.Headers.IfMatch.Add(new EntityTagHeaderValue("\"1\""));
        patchReq.Content = JsonContent.Create(new Dictionary<string, object?>
        {
            ["name"] = "Norsk Trevare & Laft AS",
            ["demo.contacts.preferredChannel"] = "phone"
        });

        using var patchRes = await client.SendAsync(patchReq);
        Assert.Equal(HttpStatusCode.OK, patchRes.StatusCode);
        Assert.Equal("\"2\"", patchRes.Headers.ETag?.Tag);

        // 6. Verify stale If-Match (row_version = 1) returns 412 Precondition Failed
        using var stalePatchReq = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/{companyId}/core.party/{partyId}");
        stalePatchReq.Headers.IfMatch.Add(new EntityTagHeaderValue("\"1\""));
        stalePatchReq.Content = JsonContent.Create(new Dictionary<string, object?> { ["rating"] = 4 });

        using var stalePatchRes = await client.SendAsync(stalePatchReq);
        Assert.Equal(HttpStatusCode.PreconditionFailed, stalePatchRes.StatusCode);

        // 7. Execute atomic $batch (Create a demo.contactnote referencing the party + Update party rating)
        var batchOps = new object[]
        {
            new
            {
                method = "Create",
                objectId = "demo.contactnote",
                fields = new Dictionary<string, object?>
                {
                    ["party"] = partyId,
                    ["text"] = "Avtalt leveringsplan for Q4",
                    ["followUp"] = "2026-11-15"
                }
            },
            new
            {
                method = "Update",
                objectId = "core.party",
                id = partyId,
                rowVersion = 2L,
                fields = new Dictionary<string, object?>
                {
                    ["rating"] = 4
                }
            }
        };

        using var batchRes = await client.PostAsJsonAsync($"/api/v1/{companyId}/$batch", batchOps);
        Assert.Equal(HttpStatusCode.OK, batchRes.StatusCode);
    }

    [Fact]
    public async Task AuditTrail_CapturesInsertUpdateDelete_AndRejectsUpdateOrDeleteByAppRole()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var token = await LoginAsync(client, DemoTenantSeeder.DemoUserSubject, "demo@demohandel.no");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var yamlContent = new StringContent(SampleManifestYaml, Encoding.UTF8, "application/x-yaml");
        using var metaRes = await client.PostAsync("/api/v1/metadata/yaml", yamlContent);
        metaRes.EnsureSuccessStatusCode();

        var companyId = DemoTenantSeeder.DemoCompanyId;

        // 1. Create record (INSERT)
        using var createRes = await client.PostAsJsonAsync(
            $"/api/v1/{companyId}/core.party",
            new Dictionary<string, object?>
            {
                ["partyNumber"] = "P-AUDIT-1",
                ["name"] = "Revisjonstest AS"
            });
        createRes.EnsureSuccessStatusCode();
        using var createdDoc = JsonDocument.Parse(await createRes.Content.ReadAsStringAsync());
        var recordId = createdDoc.RootElement.GetProperty("id").GetGuid();

        // 2. Update record (UPDATE)
        using (var patchReq = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/{companyId}/core.party/{recordId}"))
        {
            patchReq.Headers.IfMatch.Add(new EntityTagHeaderValue("\"1\""));
            patchReq.Content = JsonContent.Create(new Dictionary<string, object?>
            {
                ["name"] = "Revisjonstest Oppdatert AS"
            });
            using var patchRes = await client.SendAsync(patchReq);
            patchRes.EnsureSuccessStatusCode();
        }

        // 3. Delete record (DELETE)
        using (var delReq = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/{companyId}/core.party/{recordId}"))
        {
            delReq.Headers.IfMatch.Add(new EntityTagHeaderValue("\"2\""));
            using var delRes = await client.SendAsync(delReq);
            Assert.Equal(HttpStatusCode.NoContent, delRes.StatusCode);
        }

        // 4. Read audit trail via REST and verify INSERT, UPDATE, and DELETE entries
        using var auditRes = await client.GetAsync($"/api/v1/{companyId}/core.party/{recordId}/audit");
        Assert.Equal(HttpStatusCode.OK, auditRes.StatusCode);

        using var auditDoc = JsonDocument.Parse(await auditRes.Content.ReadAsStringAsync());
        var entries = auditDoc.RootElement.EnumerateArray().ToList();
        Assert.Equal(3, entries.Count);

        Assert.Equal("INSERT", entries[0].GetProperty("operation").GetString());
        Assert.Equal(JsonValueKind.Null, entries[0].GetProperty("oldData").ValueKind);
        Assert.Equal("Revisjonstest AS", entries[0].GetProperty("newData").GetProperty("name").GetString());
        Assert.Equal(DemoTenantSeeder.DemoUserId, entries[0].GetProperty("changedBy").GetGuid());

        Assert.Equal("UPDATE", entries[1].GetProperty("operation").GetString());
        Assert.Equal("Revisjonstest AS", entries[1].GetProperty("oldData").GetProperty("name").GetString());
        Assert.Equal("Revisjonstest Oppdatert AS", entries[1].GetProperty("newData").GetProperty("name").GetString());

        Assert.Equal("DELETE", entries[2].GetProperty("operation").GetString());
        Assert.Equal("Revisjonstest Oppdatert AS", entries[2].GetProperty("oldData").GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, entries[2].GetProperty("newData").ValueKind);
        Assert.Equal(DemoTenantSeeder.DemoUserId, entries[2].GetProperty("changedBy").GetGuid());

        // 5. Prove that the application database role (erp_app) CANNOT update or delete audit rows
        var tenantContext = new TenantContext();
        await using var appDb = fixture.CreateAppDbContext(tenantContext);
        using (tenantContext.BeginScope(DemoTenantSeeder.DemoTenantId, DemoTenantSeeder.DemoUserId))
        {
            var updateEx = await Assert.ThrowsAsync<PostgresException>(() =>
                appDb.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE core.party_audit SET operation = 'TAMPERED' WHERE record_id = {recordId};"));
            Assert.Equal("42501", updateEx.SqlState);

            var deleteEx = await Assert.ThrowsAsync<PostgresException>(() =>
                appDb.Database.ExecuteSqlInterpolatedAsync(
                    $"DELETE FROM core.party_audit WHERE record_id = {recordId};"));
            Assert.Equal("42501", deleteEx.SqlState);
        }
    }

    [Fact]
    public async Task DynamicMetadataTables_EnforceTenantIsolationViaPostgresRls()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var tokenTenant1 = await LoginAsync(client, DemoTenantSeeder.DemoUserSubject, "demo@demohandel.no");
        var tokenTenant2 = await LoginAsync(client, DemoTenantSeeder.SecondUserSubject, "anna@nordiskimport.no");

        // Register core.party in both tenants and create a record in Tenant 1
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenTenant1);
        using (var yaml1 = new StringContent(SampleManifestYaml, Encoding.UTF8, "application/x-yaml"))
        {
            (await client.PostAsync("/api/v1/metadata/yaml", yaml1)).EnsureSuccessStatusCode();
        }

        using var createRes = await client.PostAsJsonAsync(
            $"/api/v1/{DemoTenantSeeder.DemoCompanyId}/core.party",
            new Dictionary<string, object?>
            {
                ["partyNumber"] = "T1-SECRET",
                ["name"] = "Kun For Tenant 1 AS"
            });
        createRes.EnsureSuccessStatusCode();

        // Register core.party in Tenant 2 and query all records
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokenTenant2);
        using (var yaml2 = new StringContent(SampleManifestYaml, Encoding.UTF8, "application/x-yaml"))
        {
            (await client.PostAsync("/api/v1/metadata/yaml", yaml2)).EnsureSuccessStatusCode();
        }

        using var listRes2 = await client.GetAsync($"/api/v1/{DemoTenantSeeder.SecondCompanyId}/core.party");
        Assert.Equal(HttpStatusCode.OK, listRes2.StatusCode);

        using var doc2 = JsonDocument.Parse(await listRes2.Content.ReadAsStringAsync());
        Assert.Empty(doc2.RootElement.EnumerateArray());
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

    private static async Task<string> LoginAsync(HttpClient client, string subject, string email)
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

        var externalToken = new JwtSecurityTokenHandler().WriteToken(jwt);

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/token");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", externalToken);
        req.Content = JsonContent.Create(new TokenExchangeRequest());

        using var res = await client.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<TokenExchangeResponse>();
        return body!.AccessToken;
    }
}
