using Erp.Client.Models;
using Erp.Client.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Erp.Core.Tests;

[Collection(PostgresDatabaseCollection.Name)]
public sealed class GenericClientTests(PostgresDatabaseFixture fixture)
{
    private const string TestOidcSigningKey = "test-only-external-oidc-signing-key-at-least-32-bytes!";
    private const string TestOidcIssuer = "https://oidc.test.local/realms/erp-dev";
    private const string TestOidcAudience = "erp-web";

    [Fact]
    public void ClientFieldValidator_And_I18nService_MirrorMetadataAndTranslateNbAndEn()
    {
        var i18n = new I18nService();
        i18n.LoadAppTranslations(new Dictionary<string, string>
        {
            ["parties.list.title"] = "Parter (kunder og leverandører)",
            ["parties.fields.organizationNumber"] = "Organisasjonsnummer"
        });

        Assert.Equal("nb", i18n.CurrentLocale);
        Assert.Equal("Parter (kunder og leverandører)", i18n.T("parties.list.title"));
        Assert.Equal("Ny post", i18n.T("ui.action.new"));

        var title = i18n.FormatRecordTitle(
            "{partyNumber} – {name}",
            new Dictionary<string, object?>
            {
                ["partyNumber"] = "K-100",
                ["name"] = "Norsk Trevare AS"
            },
            "Part");
        Assert.Equal("K-100 – Norsk Trevare AS", title);

        var partyObj = new ClientObjectDefinition
        {
            Id = "core.party",
            OwnerApp = "core.parties",
            Label = "Part",
            Fields =
            [
                new ClientFieldDefinition
                {
                    Name = "partyType",
                    OwnerApp = "core.parties",
                    Type = ClientFieldType.Enum,
                    Required = true
                },
                new ClientFieldDefinition
                {
                    Name = "organizationNumber",
                    OwnerApp = "core.parties",
                    Type = ClientFieldType.String,
                    Label = "parties.fields.organizationNumber",
                    Pattern = "^[0-9]{9}$",
                    RequiredWhen = new ClientFilterCondition
                    {
                        Field = "partyType",
                        Operator = ClientFilterOperator.Eq,
                        Value = "organization"
                    }
                }
            ]
        };

        // Missing organizationNumber when partyType == organization fails client validation
        var errorsMissingOrg = ClientFieldValidator.ValidateRecord(
            partyObj,
            new Dictionary<string, object?> { ["partyType"] = "organization" },
            i18n);
        Assert.True(errorsMissingOrg.ContainsKey("organizationNumber"));

        // Invalid pattern fails client validation
        var errorsBadPattern = ClientFieldValidator.ValidateRecord(
            partyObj,
            new Dictionary<string, object?>
            {
                ["partyType"] = "organization",
                ["organizationNumber"] = "123"
            },
            i18n);
        Assert.True(errorsBadPattern.ContainsKey("organizationNumber"));

        // Valid 9-digit organizationNumber passes client validation
        var errorsValid = ClientFieldValidator.ValidateRecord(
            partyObj,
            new Dictionary<string, object?>
            {
                ["partyType"] = "organization",
                ["organizationNumber"] = "987654321"
            },
            i18n);
        Assert.Empty(errorsValid);

        // Person without organizationNumber passes client validation
        var errorsPerson = ClientFieldValidator.ValidateRecord(
            partyObj,
            new Dictionary<string, object?> { ["partyType"] = "person" },
            i18n);
        Assert.Empty(errorsPerson);
    }

    [Fact]
    public async Task ErpApiClient_EndToEnd_ListCardSublistStatusTransitionAndBulkDelete()
    {
        await using var factory = CreateFactory();
        using var httpClient = factory.CreateClient();
        var i18n = new I18nService();
        var api = new ErpApiClient(httpClient, i18n);

        // 1. Authenticate via DevLogin
        var token = await api.DevLoginAsync();
        Assert.NotNull(token);
        Assert.NotNull(api.CurrentSession);

        // 2. Install core.parties declarative app via client API
        var installed = await api.InstallBuiltInAppAsync("core.parties");
        Assert.True(installed);

        try
        {
            // 3. Verify metadata, pages, and i18n (nb & en)
            var pages = await api.GetPagesAsync();
            var listPage = Assert.Single(pages, p => p.Id == "core.party-list");
            var cardPage = Assert.Single(pages, p => p.Id == "core.party-card");
            Assert.Equal("list", listPage.Type);
            Assert.Equal("card", cardPage.Type);
            Assert.Equal(2, cardPage.Tabs.Count);
            Assert.NotEmpty(cardPage.Actions);

            Assert.Equal("Parter (kunder og leverandører)", i18n.T(listPage.Title));
            await api.SwitchLocaleAsync("en");
            Assert.Equal("Parties (customers and suppliers)", i18n.T(listPage.Title));
            await api.SwitchLocaleAsync("nb");

            var partyObj = await api.GetObjectAsync("core.party");
            Assert.NotNull(partyObj);
            Assert.NotNull(partyObj.Status);

            // 4. Create two parties via client
            var party1 = await api.CreateRecordAsync("core.party", new Dictionary<string, object?>
            {
                ["partyNumber"] = "P-4001",
                ["name"] = "Fjord Handel AS",
                ["partyType"] = "organization",
                ["role"] = "customer",
                ["organizationNumber"] = "912345678",
                ["email"] = "kontakt@fjordhandel.no"
            });

            var party2 = await api.CreateRecordAsync("core.party", new Dictionary<string, object?>
            {
                ["partyNumber"] = "P-4002",
                ["name"] = "Fjell Sport AS",
                ["partyType"] = "organization",
                ["role"] = "supplier",
                ["organizationNumber"] = "923456789",
                ["email"] = "post@fjellsport.no"
            });

            // 5. Search, filter, and sort on list view
            var searchFilter = ErpApiClient.BuildSearchAndCustomFilter(partyObj, "Fjord", "role eq 'customer'");
            var filteredList = await api.QueryRecordsAsync("core.party", filter: searchFilter, orderBy: "partyNumber asc");
            var matched = Assert.Single(filteredList);
            Assert.Equal(party1.Id, matched.Id);

            // 6. Edit party on card view
            var updatedParty1 = await api.UpdateRecordAsync("core.party", party1.Id, party1.RowVersion, new Dictionary<string, object?>
            {
                ["phone"] = "+47 22 33 44 55"
            });
            Assert.Equal(2L, updatedParty1.RowVersion);
            Assert.Equal("+47 22 33 44 55", I18nService.UnwrapDisplayString(updatedParty1.Fields["phone"]));

            // 7. Create child address & contact person in card sublist tabs
            var addr = await api.CreateRecordAsync("core.partyaddress", new Dictionary<string, object?>
            {
                ["party"] = party1.Id,
                ["addressType"] = "delivery",
                ["streetLine1"] = "Bryggen 5",
                ["postalCode"] = "5003",
                ["city"] = "Bergen",
                ["countryCode"] = "NO",
                ["isPrimary"] = true
            });
            Assert.NotEqual(Guid.Empty, addr.Id);

            var addressesForParty1 = await api.QueryRecordsAsync("core.partyaddress", filter: $"party eq '{party1.Id:D}'");
            Assert.Single(addressesForParty1);

            // 8. Execute status transitions (active -> blocked [locksRecord: true] -> unblock -> active)
            var blocked = await api.ExecuteTransitionAsync("core.party", party1.Id, "block", updatedParty1.RowVersion);
            Assert.Equal("blocked", I18nService.UnwrapDisplayString(blocked.Fields["status"]));

            // While locked in 'blocked' status, normal UpdateRecordAsync is rejected
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                api.UpdateRecordAsync("core.party", party1.Id, blocked.RowVersion, new Dictionary<string, object?>
                {
                    ["name"] = "Ulovlig endring på sperret part"
                }));

            var unblocked = await api.ExecuteTransitionAsync("core.party", party1.Id, "unblock", blocked.RowVersion);
            Assert.Equal("active", I18nService.UnwrapDisplayString(unblocked.Fields["status"]));

            // 9. Bulk delete both parties via $batch
            await api.ExecuteBatchAsync(
            [
                new ClientBatchOperation
                {
                    Method = ClientBatchMethod.Delete,
                    ObjectId = "core.party",
                    Id = party1.Id,
                    RowVersion = unblocked.RowVersion
                },
                new ClientBatchOperation
                {
                    Method = ClientBatchMethod.Delete,
                    ObjectId = "core.party",
                    Id = party2.Id,
                    RowVersion = party2.RowVersion
                }
            ]);

            var remaining = await api.QueryRecordsAsync("core.party", filter: "partyNumber in ('P-4001', 'P-4002')");
            Assert.Empty(remaining);
        }
        finally
        {
            var uninstalled = await api.UninstallAppAsync("core.parties");
            Assert.True(uninstalled);
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

