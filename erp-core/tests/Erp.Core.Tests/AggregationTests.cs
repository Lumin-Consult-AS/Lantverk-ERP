using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Erp.Core.Tests;

/// <summary>Aggregation in the data API (ADR-0016): sums and counts with grouping, under the normal access rules.</summary>
[Collection(PostgresDatabaseCollection.Name)]
public sealed class AggregationTests(PostgresDatabaseFixture fixture)
{
    private const string TestOidcSigningKey = "test-only-external-oidc-signing-key-at-least-32-bytes!";
    private const string TestOidcIssuer = "https://oidc.test.local/realms/erp-dev";
    private const string TestOidcAudience = "erp-web";

    private const string Yaml = """
        id: demo.sums
        version: 1.0.0

        objects:
          demo.posting:
            ownerApp: demo.sums
            label: Linje
            fields:
              account: { type: string, maxLength: 20, required: true }
              amount:  { type: decimal, scale: 2, required: true }
              count:   { type: integer, default: 1 }
              memo:    { type: text }
        """;

    [Fact]
    public async Task Aggregation_SumsCountsAndGroups_WithFiltersAndValidation()
    {
        await using var factory = CreateFactory();
        using var client = await LoginAsync(factory, null);
        await RegisterAsync(client);

        var tag = Guid.NewGuid().ToString("N")[..6];
        var a = $"A{tag}";
        var b = $"B{tag}";
        await CreateAsync(client, a, 100.50m, 2);
        await CreateAsync(client, a, 200.25m, 3);
        await CreateAsync(client, b, -50m, 1);

        // Sum, count, min and max per account, ordered by the group
        var rows = await AggregateAsync(client, new
        {
            groupBy = new[] { "account" },
            filter = $"account in ('{a}', '{b}')",
            measures = new object[]
            {
                new { function = "sum", field = "amount", @as = "total" },
                new { function = "count", @as = "lines" },
                new { function = "max", field = "count", @as = "most" },
                new { function = "min", field = "amount", @as = "least" }
            }
        });
        Assert.Equal(2, rows.Count);
        Assert.Equal(a, rows[0].GetProperty("account").GetString());
        Assert.Equal(300.75m, rows[0].GetProperty("total").GetDecimal());
        Assert.Equal(2, rows[0].GetProperty("lines").GetInt32());
        Assert.Equal(3, rows[0].GetProperty("most").GetInt32());
        Assert.Equal(100.50m, rows[0].GetProperty("least").GetDecimal());
        Assert.Equal(b, rows[1].GetProperty("account").GetString());
        Assert.Equal(-50m, rows[1].GetProperty("total").GetDecimal());

        // No grouping gives one row for everything the filter matches
        var all = await AggregateAsync(client, new
        {
            filter = $"account eq '{a}'",
            measures = new object[] { new { function = "sum", field = "amount", @as = "total" }, new { function = "avg", field = "amount", @as = "average" } }
        });
        Assert.Single(all);
        Assert.Equal(300.75m, all[0].GetProperty("total").GetDecimal());
        Assert.Equal(150.375m, Math.Round(all[0].GetProperty("average").GetDecimal(), 3));

        // Wrong requests give clear errors, not a database error
        foreach (var bad in new object[]
                 {
                     new { measures = Array.Empty<object>() },
                     new { measures = new object[] { new { function = "median", field = "amount", @as = "x" } } },
                     new { measures = new object[] { new { function = "sum", field = "account", @as = "x" } } },
                     new { measures = new object[] { new { function = "sum", field = "nope", @as = "x" } } },
                     new { measures = new object[] { new { function = "count", @as = "bad alias; drop table" } } },
                     new { groupBy = new[] { "memo" }, measures = new object[] { new { function = "count", @as = "n" } } },
                     new { groupBy = new[] { "account" }, measures = new object[] { new { function = "count", @as = "account" } } }
                 })
        {
            using var res = await client.PostAsJsonAsync(Url, bad);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }
    }

    [Fact]
    public async Task Aggregation_OnlySeesTheCallersOwnTenant()
    {
        await using var factory = CreateFactory();
        using var first = await LoginAsync(factory, null);
        await RegisterAsync(first);
        var tag = $"T{Guid.NewGuid():N}"[..10];
        await CreateAsync(first, tag, 999m, 1);

        // The second tenant has the same object but none of the first tenant's rows
        using var second = await LoginAsync(factory, DemoTenantSeeder.SecondUserSubject);
        await RegisterAsync(second, DemoTenantSeeder.SecondCompanyId);
        await CreateAsync(second, tag, 1m, 1, DemoTenantSeeder.SecondCompanyId);

        var filter = $"account eq '{tag}'";
        var measures = new object[] { new { function = "sum", field = "amount", @as = "total" }, new { function = "count", @as = "lines" } };
        var mine = await AggregateAsync(first, new { filter, measures });
        var theirs = await AggregateAsync(second, new { filter, measures }, DemoTenantSeeder.SecondCompanyId);
        Assert.Equal(999m, mine[0].GetProperty("total").GetDecimal());
        Assert.Equal(1, mine[0].GetProperty("lines").GetInt32());
        Assert.Equal(1m, theirs[0].GetProperty("total").GetDecimal());
        Assert.Equal(1, theirs[0].GetProperty("lines").GetInt32());
    }

    // ------------------------------------------------------------------ helpers

    private static string Url => UrlFor(DemoTenantSeeder.DemoCompanyId);

    private static string UrlFor(Guid company) => $"/api/v1/{company}/demo.posting/aggregate";

    private static async Task RegisterAsync(HttpClient client, Guid? company = null)
    {
        using var content = new StringContent(Yaml, Encoding.UTF8, "application/x-yaml");
        using var res = await client.PostAsync("/api/v1/metadata/yaml", content);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
    }

    private static async Task CreateAsync(HttpClient client, string account, decimal amount, int count, Guid? company = null)
    {
        using var res = await client.PostAsJsonAsync(
            $"/api/v1/{company ?? DemoTenantSeeder.DemoCompanyId}/demo.posting", new { account, amount, count });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
    }

    private static async Task<List<JsonElement>> AggregateAsync(HttpClient client, object body, Guid? company = null)
    {
        using var res = await client.PostAsJsonAsync(UrlFor(company ?? DemoTenantSeeder.DemoCompanyId), body);
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("rows").EnumerateArray().Select(r => r.Clone()).ToList();
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
