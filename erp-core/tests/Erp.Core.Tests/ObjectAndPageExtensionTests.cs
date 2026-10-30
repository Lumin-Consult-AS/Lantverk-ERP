using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.Client.Services;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace Erp.Core.Tests;

[Collection(PostgresDatabaseCollection.Name)]
public sealed class ObjectAndPageExtensionTests(PostgresDatabaseFixture fixture)
{
    private const string TestOidcSigningKey = "test-only-external-oidc-signing-key-at-least-32-bytes!";
    private const string TestOidcIssuer = "https://oidc.test.local/realms/erp-dev";
    private const string TestOidcAudience = "erp-web";

    [Fact]
    public async Task DemoContacts_ExtendsPartyAndCardPage_AndUninstallRemovesFromUiWhileRetainingOrphanedColumn()
    {
        await using var factory = CreateFactory();
        using var httpClient = factory.CreateClient();
        var i18n = new I18nService();
        var api = new ErpApiClient(httpClient, i18n);

        var token = await api.DevLoginAsync();
        Assert.NotNull(token);

        // 1. Install base app core.parties and sample extension app demo.contacts
        Assert.True(await api.InstallBuiltInAppAsync("core.parties"));
        Assert.True(await api.InstallBuiltInAppAsync("demo.contacts"));

        Guid createdPartyId = Guid.Empty;

        try
        {
            // 2. Verify effective core.party-card page includes extended field after email and Kontaktnotater tab
            var partyCard = await api.GetPageAsync("core.party-card");
            Assert.NotNull(partyCard);

            var contactGroup = partyCard.Groups.Single(g => g.Label == "parties.card.contact");
            var emailIdx = contactGroup.Fields.IndexOf("email");
            var extFieldIdx = contactGroup.Fields.IndexOf("demo.contacts.preferredChannel");
            Assert.True(emailIdx >= 0);
            Assert.Equal(emailIdx + 1, extFieldIdx);

            var notesTab = Assert.Single(partyCard.Tabs, t => t.Label == "contacts.tabs.contactNotes");
            Assert.Equal("demo.contactnote", notesTab.Object);
            Assert.Equal("Kontaktnotater", i18n.T(notesTab.Label));
            Assert.Equal("Foretrukket kanal", i18n.T("contacts.fields.preferredChannel"));

            // 3. Create a party with the extension field demo.contacts.preferredChannel
            var party = await api.CreateRecordAsync("core.party", new Dictionary<string, object?>
            {
                ["partyNumber"] = "EXT-5001",
                ["name"] = "Vestland Bygg AS",
                ["partyType"] = "organization",
                ["role"] = "customer",
                ["organizationNumber"] = "934567890",
                ["email"] = "post@vestlandbygg.no",
                ["demo.contacts.preferredChannel"] = "sms"
            });
            createdPartyId = party.Id;
            Assert.Equal("sms", I18nService.UnwrapDisplayString(party.Fields["demo.contacts.preferredChannel"]));

            // 4. Create a demo.contactnote for this party and query it via the tab filter
            var note = await api.CreateRecordAsync("demo.contactnote", new Dictionary<string, object?>
            {
                ["party"] = party.Id,
                ["followUp"] = "2026-11-15",
                ["text"] = "Ring daglig leder om rammeavtale"
            });
            Assert.NotEqual(Guid.Empty, note.Id);

            var notesForParty = await api.QueryRecordsAsync("demo.contactnote", filter: $"party eq '{party.Id:D}'");
            var fetchedNote = Assert.Single(notesForParty);
            Assert.Equal("Ring daglig leder om rammeavtale", I18nService.UnwrapDisplayString(fetchedNote.Fields["text"]));

            // 5. Uninstall demo.contacts
            Assert.True(await api.UninstallAppAsync("demo.contacts"));

            // 6. Verify demo.contacts field and tab are removed from effective UI page
            var partyCardAfterUninstall = await api.GetPageAsync("core.party-card");
            Assert.NotNull(partyCardAfterUninstall);

            var contactGroupAfter = partyCardAfterUninstall.Groups.Single(g => g.Label == "parties.card.contact");
            Assert.DoesNotContain("demo.contacts.preferredChannel", contactGroupAfter.Fields);
            Assert.DoesNotContain(partyCardAfterUninstall.Tabs, t => t.Label == "contacts.tabs.contactNotes");

            // 7. Verify field is removed from active metadata, but recorded in OrphanedColumns and retained in PostgreSQL
            using var objRes = await httpClient.GetAsync("/api/v1/metadata/objects/core.party");
            objRes.EnsureSuccessStatusCode();
            using var objDoc = JsonDocument.Parse(await objRes.Content.ReadAsStringAsync());
            var activeFieldNames = objDoc.RootElement.GetProperty("fields")
                .EnumerateArray()
                .Select(f => f.GetProperty("name").GetString())
                .ToList();
            Assert.DoesNotContain("preferredChannel", activeFieldNames);

            var orphanedCols = objDoc.RootElement.GetProperty("orphanedColumns").EnumerateArray().ToList();
            var orphaned = Assert.Single(orphanedCols, o =>
                o.GetProperty("columnName").GetString() == "demo_contacts_preferred_channel");
            Assert.Equal("demo.contacts", orphaned.GetProperty("formerOwnerApp").GetString());

            // Verify physical PostgreSQL column and data still exist during the grace period
            await using var conn = new NpgsqlConnection(fixture.MigrationConnectionString);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                """SELECT demo_contacts_preferred_channel FROM "core"."party" WHERE id = @id;""",
                conn);
            cmd.Parameters.AddWithValue("id", createdPartyId);
            var retainedValue = await cmd.ExecuteScalarAsync();
            Assert.Equal("sms", retainedValue?.ToString());
        }
        finally
        {
            await api.UninstallAppAsync("demo.contacts");
            await api.UninstallAppAsync("core.parties");
        }
    }

    [Fact]
    public async Task MutualFieldOwnership_PreventsOwnerFromModifyingExtensionFields_AndViceVersa()
    {
        await using var factory = CreateFactory();
        using var httpClient = factory.CreateClient();
        var i18n = new I18nService();
        var api = new ErpApiClient(httpClient, i18n);

        await api.DevLoginAsync();
        Assert.True(await api.InstallBuiltInAppAsync("core.parties"));
        Assert.True(await api.InstallBuiltInAppAsync("demo.contacts"));

        try
        {
            // 1. Object owner (core.parties) attempting to modify demo.contacts's field must fail
            const string ownerTamperingExtensionYaml = """
                id: core.parties
                version: 1.0.1
                objects:
                  core.party:
                    label: Part
                    fields:
                      partyNumber: { type: string, required: true }
                      name: { type: string, required: true }
                      preferredChannel:
                        type: string
                        ownerApp: demo.contacts
                """;

            using var ownerTamperRes = await httpClient.PostAsJsonAsync(
                "/api/v1/metadata/yaml",
                new { yaml = ownerTamperingExtensionYaml });
            Assert.Equal(HttpStatusCode.BadRequest, ownerTamperRes.StatusCode);

            // 2. Extending app (demo.contacts) attempting to modify core.parties's field must fail
            const string extensionTamperingOwnerYaml = """
                id: demo.contacts
                version: 1.0.1
                extensions:
                  core.party:
                    fields:
                      email:
                        type: string
                        ownerApp: core.parties
                """;

            using var extTamperRes = await httpClient.PostAsJsonAsync(
                "/api/v1/metadata/yaml",
                new { yaml = extensionTamperingOwnerYaml });
            Assert.Equal(HttpStatusCode.BadRequest, extTamperRes.StatusCode);
        }
        finally
        {
            await api.UninstallAppAsync("demo.contacts");
            await api.UninstallAppAsync("core.parties");
        }
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

