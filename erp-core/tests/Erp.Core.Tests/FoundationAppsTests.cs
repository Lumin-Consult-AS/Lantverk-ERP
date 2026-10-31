using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Apps;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Erp.Core.Tests;

/// <summary>The foundation apps of phase 2 (items and dimensions): install, use, extend by another app, uninstall.</summary>
[Collection(PostgresDatabaseCollection.Name)]
public sealed class FoundationAppsTests(PostgresDatabaseFixture fixture)
{
    private const string TestOidcSigningKey = "test-only-external-oidc-signing-key-at-least-32-bytes!";
    private const string TestOidcIssuer = "https://oidc.test.local/realms/erp-dev";
    private const string TestOidcAudience = "erp-web";

    private static string Url(string objectId) => $"/api/v1/{DemoTenantSeeder.DemoCompanyId}/{objectId}";

    [Fact]
    public async Task ItemsAndDimensions_InstallAreUsableAndCanBeExtendedByAnotherApp()
    {
        await using var factory = CreateFactory();
        using var client = await LoginAsync(factory);

        try
        {
            await InstallAsync(client, "core.items");
            await InstallAsync(client, "core.dimensions");

            // ---- items: unit, group, item, barcodes
            var unit = await CreateAsync(client, "core.itemunit", new { code = "STK", name = "Stykk" });
            var group = await CreateAsync(client, "core.itemgroup", new { code = "VERKTOY", name = "Verktøy" });
            var item = await CreateAsync(client, "core.item", new
            {
                itemNumber = "V-1000",
                name = "Hammer 500 g",
                unit = Id(unit),
                itemGroup = Id(group)
            });
            Assert.Equal("goods", item.GetProperty("fields").GetProperty("itemType").GetString());
            await CreateAsync(client, "core.itembarcode", new { item = Id(item), barcode = "7035620000017", isPrimary = true });

            // Unique keys: the same item number, unit code or barcode cannot be used twice
            foreach (var (objectId, body) in new (string, object)[]
                     {
                         ("core.item", new { itemNumber = "V-1000", name = "Dobbel", unit = Id(unit) }),
                         ("core.itemunit", new { code = "STK", name = "Dobbel" }),
                         ("core.itembarcode", new { item = Id(item), barcode = "7035620000017" })
                     })
            {
                using var duplicate = await client.PostAsJsonAsync(Url(objectId), body);
                Assert.True(
                    duplicate.StatusCode == HttpStatusCode.BadRequest,
                    $"{objectId}: {(int)duplicate.StatusCode}");
            }

            // An item needs a unit
            using (var noUnit = await client.PostAsJsonAsync(Url("core.item"), new { itemNumber = "V-2000", name = "Uten enhet" }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, noUnit.StatusCode);
            }

            // ---- dimensions: a dimension with values
            var department = await CreateAsync(client, "core.dimension", new { code = "AVD", name = "Avdeling" });
            await CreateAsync(client, "core.dimensionvalue", new { dimension = Id(department), code = "10", name = "Salg", validFrom = "2026-01-01" });
            await CreateAsync(client, "core.dimensionvalue", new { dimension = Id(department), code = "20", name = "Lager" });
            using (var sameCode = await client.PostAsJsonAsync(Url("core.dimensionvalue"), new { dimension = Id(department), code = "10", name = "Dobbel" }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, sameCode.StatusCode);
            }

            // ---- the pages are available to the client
            var pages = await client.GetFromJsonAsync<JsonElement>("/api/v1/metadata/pages");
            var pageIds = pages.EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToList();
            foreach (var expected in new[] { "core.item-list", "core.item-card", "core.itemunit-list", "core.itemgroup-list", "core.dimension-list", "core.dimension-card" })
            {
                Assert.Contains(expected, pageIds);
            }

            // ---- another app adds its own field to an item, without any change in the core or in core.items
            var extension = AppPackageFormat.CreateSignedZipFromFiles(new Dictionary<string, string>
            {
                ["app.yaml"] = """
                    id: demo.itemext
                    version: 1.0.0
                    name: Varelokasjon
                    dependencies:
                      core.items: ">=1.0.0 <2.0.0"
                    extensions:
                      core.item:
                        fields:
                          shelf:
                            type: string
                            maxLength: 20
                    """
            });
            var content = new ByteArrayContent(extension);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            using (var install = await client.PostAsync("/api/v1/apps/install", content))
            {
                Assert.True(install.IsSuccessStatusCode, await install.Content.ReadAsStringAsync());
            }

            var shelved = await CreateAsync(client, "core.item", new
            {
                itemNumber = "V-3000",
                name = "Sag",
                unit = Id(unit),
                fields = (object?)null
            }, extra: new Dictionary<string, object?> { ["demo.itemext.shelf"] = "A-12" });
            Assert.Equal("A-12", shelved.GetProperty("fields").GetProperty("demo.itemext.shelf").GetString());

            // The extension app cannot be left behind: items cannot be uninstalled while it depends on them
            using (var blocked = await client.DeleteAsync("/api/v1/apps/core.items"))
            {
                Assert.NotEqual(HttpStatusCode.OK, blocked.StatusCode);
            }

            Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync("/api/v1/apps/demo.itemext")).StatusCode);
        }
        finally
        {
            await client.DeleteAsync("/api/v1/apps/demo.itemext");
            await client.DeleteAsync("/api/v1/apps/core.items");
            await client.DeleteAsync("/api/v1/apps/core.dimensions");
        }

        // Uninstalled: the pages are gone
        var after = await client.GetFromJsonAsync<JsonElement>("/api/v1/metadata/pages");
        Assert.DoesNotContain(after.EnumerateArray(), p => p.GetProperty("id").GetString() is "core.item-list" or "core.dimension-list");
    }

    private static Guid Id(JsonElement record) => record.GetProperty("id").GetGuid();

    private static async Task InstallAsync(HttpClient client, string appId)
    {
        using var res = await client.PostAsync($"/api/v1/apps/install-builtin/{appId}", null);
        Assert.True(res.IsSuccessStatusCode, $"{appId}: {await res.Content.ReadAsStringAsync()}");
    }

    private static async Task<JsonElement> CreateAsync(HttpClient client, string objectId, object body, Dictionary<string, object?>? extra = null)
    {
        object payload = body;
        if (extra is not null)
        {
            var merged = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(body))!;
            merged.Remove("fields");
            foreach (var (key, value) in extra)
            {
                merged[key] = value;
            }

            payload = merged;
        }

        using var res = await client.PostAsJsonAsync(Url(objectId), payload);
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{objectId}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
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
