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

/// <summary>Number series (ADR-0019): system-assigned, gap-free values on a field.</summary>
[Collection(PostgresDatabaseCollection.Name)]
public sealed class NumberSeriesTests(PostgresDatabaseFixture fixture)
{
    private const string TestOidcSigningKey = "test-only-external-oidc-signing-key-at-least-32-bytes!";
    private const string TestOidcIssuer = "https://oidc.test.local/realms/erp-dev";
    private const string TestOidcAudience = "erp-web";

    private const string Yaml = """
        id: demo.numbers
        version: 1.0.0

        objects:
          demo.voucher:
            ownerApp: demo.numbers
            label: Bilag
            fields:
              number:  { type: string, maxLength: 40, numberSeries: { prefix: "B-", padding: 6 } }
              title:   { type: string, maxLength: 100, required: true }
          demo.invoice:
            ownerApp: demo.numbers
            label: Faktura
            fields:
              number:  { type: string, maxLength: 40, numberSeries: { prefix: "F", padding: 4, perYear: true } }
              title:   { type: string, maxLength: 100 }
        """;

    [Fact]
    public async Task NumbersAreAssignedInOrder_ByTheSystem_AndCannotBeSetOrChanged()
    {
        await using var factory = CreateFactory();
        using var client = await LoginAsync(factory);
        await RegisterAsync(client);

        var first = await CreateAsync(client, "demo.voucher", new { title = "Første" });
        var second = await CreateAsync(client, "demo.voucher", new { title = "Andre" });
        Assert.Equal(Next(first, "B-"), Number(second));
        Assert.Matches(@"^B-\d{6}$", Number(first));

        // The year is part of a yearly series, and it has its own counter
        var invoice = await CreateAsync(client, "demo.invoice", new { title = "A" });
        Assert.Matches($@"^F{DateTime.UtcNow.Year}-0001$|^F{DateTime.UtcNow.Year}-\d{{4}}$", Number(invoice));

        // A value supplied by the caller is refused ...
        using var supplied = await client.PostAsJsonAsync(Url("demo.voucher"), new { number = "B-999999", title = "Forsøk" });
        Assert.Equal(HttpStatusCode.BadRequest, supplied.StatusCode);
        Assert.Contains("number series", await supplied.Content.ReadAsStringAsync());

        // ... and so is a change afterwards
        using var change = await PatchAsync(client, first, new { number = "B-000777" });
        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);

        // Other fields can still be changed
        using var rename = await PatchAsync(client, first, new { title = "Endret" });
        Assert.Equal(HttpStatusCode.OK, rename.StatusCode);
    }

    [Fact]
    public async Task AFailedCreate_LeavesNoGap_AndParallelCreatesGetDistinctConsecutiveNumbers()
    {
        await using var factory = CreateFactory();
        using var client = await LoginAsync(factory);
        await RegisterAsync(client);

        var before = await CreateAsync(client, "demo.voucher", new { title = "Før" });
        var start = Sequence(before);

        // 'title' is required: the create fails after the number has been taken, and the number goes back
        using var failed = await client.PostAsJsonAsync(Url("demo.voucher"), new { });
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        var after = await CreateAsync(client, "demo.voucher", new { title = "Etter" });
        Assert.Equal(start + 1, Sequence(after));

        // Ten creates at the same time: all numbers different and without holes
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => CreateAsync(client, "demo.voucher", new { title = $"P{i}" })));
        var numbers = results.Select(Sequence).OrderBy(n => n).ToList();
        Assert.Equal(Enumerable.Range((int)start + 2, 10).Select(n => (long)n), numbers);
    }

    [Fact]
    public async Task Counters_AreNotReachableDirectly_AndEachTenantHasItsOwn()
    {
        await using var factory = CreateFactory();
        using var client = await LoginAsync(factory);
        await RegisterAsync(client);
        await CreateAsync(client, "demo.voucher", new { title = "Telle" });

        await using var conn = new NpgsqlConnection(fixture.ApplicationConnectionString);
        await conn.OpenAsync();

        // The application role may call the function but not read or edit the counters
        var denied = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await using var readTx = await conn.BeginTransactionAsync();
            await using var setTenant = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{DemoTenantSeeder.DemoTenantId}', true);", conn, readTx);
            await setTenant.ExecuteNonQueryAsync();
            await using var read = new NpgsqlCommand("SELECT count(*) FROM core.number_counter;", conn, readTx);
            await read.ExecuteScalarAsync();
        });
        Assert.Equal("42501", denied.SqlState);

        // Another tenant starts at 1 on the same object field, whatever the demo tenant has used
        var stranger = Guid.NewGuid();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var set = new NpgsqlCommand($"SELECT set_config('app.tenant_id', '{stranger}', true);", conn, tx))
        {
            await set.ExecuteNonQueryAsync();
        }

        await using var next = new NpgsqlCommand("SELECT core.next_number(NULL, 'demo.voucher', 'number', 0);", conn, tx);
        Assert.Equal(1L, Convert.ToInt64(await next.ExecuteScalarAsync()));
    }

    // ------------------------------------------------------------------ helpers

    private static string Url(string objectId) => $"/api/v1/{DemoTenantSeeder.DemoCompanyId}/{objectId}";

    private static async Task RegisterAsync(HttpClient client)
    {
        using var content = new StringContent(Yaml, Encoding.UTF8, "application/x-yaml");
        using var res = await client.PostAsync("/api/v1/metadata/yaml", content);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> CreateAsync(HttpClient client, string objectId, object body)
    {
        using var res = await client.PostAsJsonAsync(Url(objectId), body);
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == HttpStatusCode.Created || res.StatusCode == HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    private static string Number(JsonElement record) => record.GetProperty("fields").GetProperty("number").GetString()!;

    private static Guid Id(JsonElement record) => record.GetProperty("id").GetGuid();

    private static async Task<HttpResponseMessage> PatchAsync(HttpClient client, JsonElement record, object body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Patch, $"{Url("demo.voucher")}/{Id(record)}");
        req.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{RowVersion(record)}\""));
        req.Content = JsonContent.Create(body);
        return await client.SendAsync(req);
    }

    private static long RowVersion(JsonElement record) => record.GetProperty("rowVersion").GetInt64();

    private static long Sequence(JsonElement record) => long.Parse(Number(record).Split('-')[^1]);

    private static string Next(JsonElement previous, string prefix) =>
        $"{prefix}{(Sequence(previous) + 1).ToString().PadLeft(6, '0')}";

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
