using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Erp.Core.Tests;

/// <summary>Immutable records (ADR-0020): posted rows cannot be changed or deleted, also not directly in the database.</summary>
[Collection(PostgresDatabaseCollection.Name)]
public sealed class ImmutableRecordsTests(PostgresDatabaseFixture fixture)
{
    private const string TestOidcSigningKey = "test-only-external-oidc-signing-key-at-least-32-bytes!";
    private const string TestOidcIssuer = "https://oidc.test.local/realms/erp-dev";
    private const string TestOidcAudience = "erp-web";

    private const string Yaml = """
        id: demo.ledger
        version: 1.0.0

        enums:
          demo.entrystatus: [draft, posted]

        objects:
          demo.entry:
            ownerApp: demo.ledger
            label: Postering
            fields:
              text:   { type: string, maxLength: 100, required: true }
              amount: { type: decimal, scale: 2, required: true }
              status: { type: enum, of: demo.entrystatus, required: true, default: draft }
            status:
              field: status
              initial: draft
              immutableIn: [posted]
              transitions:
                post: { from: draft, to: posted }
        """;

    [Fact]
    public async Task APostedRecord_CanNeitherBeChangedNorDeleted_NotEvenWithDirectSql_ButADraftCan()
    {
        await using var factory = CreateFactory();
        using var client = await LoginAsync(factory);
        using (var content = new StringContent(Yaml, Encoding.UTF8, "application/x-yaml"))
        {
            using var res = await client.PostAsync("/api/v1/metadata/yaml", content);
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        }

        var draft = await CreateAsync(client, "Utkast", 100m);

        // A draft is an ordinary record
        using (var edit = await PatchAsync(client, draft, new { text = "Rettet utkast" }))
        {
            Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
            draft = JsonDocument.Parse(await edit.Content.ReadAsStringAsync()).RootElement.Clone();
        }

        // Posting is the last change the record accepts
        using (var post = await client.PostAsync($"{Url}/{Id(draft)}/transitions/post", null))
        {
            Assert.True(post.IsSuccessStatusCode, await post.Content.ReadAsStringAsync());
            draft = JsonDocument.Parse(await post.Content.ReadAsStringAsync()).RootElement.Clone();
        }

        Assert.Equal("posted", draft.GetProperty("fields").GetProperty("status").GetString());

        using var change = await PatchAsync(client, draft, new { text = "Forsøk på endring" });
        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);
        Assert.Contains("immutable record", await change.Content.ReadAsStringAsync());

        using var delete = await DeleteAsync(client, draft);
        Assert.Equal(HttpStatusCode.BadRequest, delete.StatusCode);
        Assert.Contains("immutable record", await delete.Content.ReadAsStringAsync());

        // Direct SQL as the application role is refused by the database itself
        await using var conn = new NpgsqlConnection(fixture.ApplicationConnectionString);
        await conn.OpenAsync();
        foreach (var sql in new[]
                 {
                     "UPDATE demo.entry SET amount = 1 WHERE id = @id;",
                     "UPDATE demo.entry SET status = 'draft' WHERE id = @id;",
                     "DELETE FROM demo.entry WHERE id = @id;"
                 })
        {
            await using var tx = await conn.BeginTransactionAsync();
            await using (var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{DemoTenantSeeder.DemoTenantId}', true);", conn, tx))
            {
                await set.ExecuteNonQueryAsync();
            }

            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            cmd.Parameters.AddWithValue("id", Id(draft));
            var error = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
            Assert.Equal("23000", error.SqlState);
        }

        // The record is unchanged, and a correction is a new record
        var current = await client.GetFromJsonAsync<JsonElement>($"{Url}/{Id(draft)}");
        Assert.Equal("Rettet utkast", current.GetProperty("fields").GetProperty("text").GetString());
        var correction = await CreateAsync(client, "Motpostering", -100m);
        Assert.Equal("draft", correction.GetProperty("fields").GetProperty("status").GetString());

        // A draft can still be deleted
        using var deleteDraft = await DeleteAsync(client, correction);
        Assert.Equal(HttpStatusCode.NoContent, deleteDraft.StatusCode);
    }

    private static string Url => $"/api/v1/{DemoTenantSeeder.DemoCompanyId}/demo.entry";

    private static Guid Id(JsonElement record) => record.GetProperty("id").GetGuid();

    private static long RowVersion(JsonElement record) => record.GetProperty("rowVersion").GetInt64();

    private static async Task<JsonElement> CreateAsync(HttpClient client, string text, decimal amount)
    {
        using var res = await client.PostAsJsonAsync(Url, new { text, amount });
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, JsonElement record, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Patch, $"{Url}/{Id(record)}");
        req.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{RowVersion(record)}\""));
        req.Content = JsonContent.Create(body);
        return await client.SendAsync(req);
    }

    private static async Task<HttpResponseMessage> DeleteAsync(HttpClient client, JsonElement record)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{Url}/{Id(record)}");
        req.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{RowVersion(record)}\""));
        return await client.SendAsync(req);
    }

    private static async Task<HttpClient> LoginAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        using var res = await client.PostAsJsonAsync("/api/v1/auth/dev-login", new { });
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
