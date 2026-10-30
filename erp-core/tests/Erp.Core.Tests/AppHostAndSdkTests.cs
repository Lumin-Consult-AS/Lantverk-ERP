using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Demo.Contacts;
using Erp.Client.Services;
using Erp.Core.Infrastructure.Apps;
using Erp.Core.Infrastructure.Persistence;
using Erp.Sdk;
using Erp.Sdk.Contracts.V1;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Erp.Core.Tests;

[Collection(PostgresDatabaseCollection.Name)]
public sealed class AppHostAndSdkTests(PostgresDatabaseFixture fixture)
{
    private const string TestOidcSigningKey = "test-only-external-oidc-signing-key-at-least-32-bytes!";
    private const string TestOidcIssuer = "https://oidc.test.local/realms/erp-dev";
    private const string TestOidcAudience = "erp-web";

    [Fact]
    public async Task DemoContactsContainer_RejectsPastFollowUpDateOnValidate_AndDeletesNotesWhenPartyIsDeletedOnEvent()
    {
        await using var coreFactory = CreateCoreFactory();
        using var coreHttpClient = coreFactory.CreateClient();

        // Start isolated gRPC container host for Demo.Contacts.App (built strictly against Erp.Sdk)
        await using var contactsHost = await StartIsolatedAppContainerAsync<ContactsApp>(
            () => coreFactory.Server.CreateHandler());

        var orchestrator = coreFactory.Services.GetRequiredService<KubernetesAppOrchestrator>();
        orchestrator.RegisterEndpoint(
            "demo.contacts",
            "1.0.0",
            "http://demo-contacts-container:50051",
            () => contactsHost.GetTestServer().CreateHandler());

        var i18n = new I18nService();
        var api = new ErpApiClient(coreHttpClient, i18n);

        var token = await api.DevLoginAsync();
        Assert.NotNull(token);

        Assert.True(await api.InstallBuiltInAppAsync("core.parties"));
        Assert.True(await api.InstallBuiltInAppAsync("demo.contacts"));

        try
        {
            // 1. Verify Kubernetes Deployment and NetworkPolicy were generated from runtime + egress whitelist
            var deploymentSpec = orchestrator.GetDeploymentSpec("demo.contacts", "1.0.0");
            Assert.NotNull(deploymentSpec);
            Assert.Equal("ghcr.io/partner/demo-contacts:1.0.0", deploymentSpec.Image);
            Assert.Equal("250m", deploymentSpec.CpuLimit);
            Assert.Equal("256Mi", deploymentSpec.MemoryLimit);
            Assert.Contains("api.hubspot.com:443", deploymentSpec.AllowedEgress);
            Assert.Contains("kind: NetworkPolicy", deploymentSpec.KubernetesNetworkPolicyYaml);
            Assert.Contains("api.hubspot.com:443", deploymentSpec.KubernetesNetworkPolicyYaml);

            // 2. Create a party in core.parties
            var party = await api.CreateRecordAsync("core.party", new Dictionary<string, object?>
            {
                ["partyNumber"] = "P-CONT-100",
                ["name"] = "Fjord Handel AS",
                ["partyType"] = "organization",
                ["role"] = "customer",
                ["organizationNumber"] = "987654321"
            });
            Assert.NotEqual(Guid.Empty, party.Id);

            // 3. Attempt to create a demo.contactnote with followUp in the past -> must be rejected by ContactsApp.ValidateNote (OnValidate)
            var yesterday = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            using var invalidRes = await coreHttpClient.PostAsJsonAsync(
                $"/api/v1/{DemoTenantSeeder.DemoCompanyId}/demo.contactnote",
                new Dictionary<string, object?>
                {
                    ["party"] = party.Id,
                    ["text"] = "Notat med dato i fortiden",
                    ["followUp"] = yesterday
                });

            Assert.Equal(HttpStatusCode.BadRequest, invalidRes.StatusCode);
            using var errDoc = JsonDocument.Parse(await invalidRes.Content.ReadAsStringAsync());
            Assert.Equal("followUp", errDoc.RootElement.GetProperty("field").GetString());
            Assert.Contains("Oppfølgingsdato kan ikke være tilbake i tid", errDoc.RootElement.GetProperty("error").GetString());

            // 4. Create two demo.contactnote records with followUp in the future -> must succeed
            var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var note1 = await api.CreateRecordAsync("demo.contactnote", new Dictionary<string, object?>
            {
                ["party"] = party.Id,
                ["text"] = "Følg opp tilbud på laftetømmer",
                ["followUp"] = tomorrow
            });
            var note2 = await api.CreateRecordAsync("demo.contactnote", new Dictionary<string, object?>
            {
                ["party"] = party.Id,
                ["text"] = "Send oppdatert prisliste",
                ["followUp"] = tomorrow
            });
            Assert.NotEqual(Guid.Empty, note1.Id);
            Assert.NotEqual(Guid.Empty, note2.Id);

            var notesBeforeDelete = await api.QueryRecordsAsync("demo.contactnote", filter: $"party eq '{party.Id:D}'");
            Assert.Equal(2, notesBeforeDelete.Count);

            // 5. Delete the party -> triggers core.party.deleted@1 -> ContactsApp.OnPartyDeleted deletes notes via gRPC callback to Core DataService
            using var deleteReq = new HttpRequestMessage(
                HttpMethod.Delete,
                $"/api/v1/{DemoTenantSeeder.DemoCompanyId}/core.party/{party.Id}");
            deleteReq.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{party.RowVersion}\""));
            using var deleteRes = await coreHttpClient.SendAsync(deleteReq);
            Assert.Equal(HttpStatusCode.NoContent, deleteRes.StatusCode);

            // 6. Events are delivered asynchronously from the outbox; wait until OnPartyDeleted has removed the notes
            var notesAfterDelete = await EventBusTestHelpers.WaitForAsync(
                () => api.QueryRecordsAsync("demo.contactnote", filter: $"party eq '{party.Id:D}'"),
                notes => notes.Count == 0);
            Assert.Empty(notesAfterDelete);

            // 7. The event is in the log with a delivery to demo.contacts, and can be replayed to the app
            using var eventsDoc = JsonDocument.Parse(await coreHttpClient.GetStringAsync("/api/v1/events?name=core.party.deleted@1"));
            var deletedEvent = Assert.Single(
                eventsDoc.RootElement.EnumerateArray(),
                e => e.GetProperty("aggregateId").GetString() == party.Id.ToString("D"));
            var deletedEventId = deletedEvent.GetProperty("id").GetGuid();

            using var replayRes = await coreHttpClient.PostAsJsonAsync(
                $"/api/v1/events/{deletedEventId}/replay",
                new { app = "demo.contacts" });
            Assert.Equal(HttpStatusCode.Accepted, replayRes.StatusCode);

            var detail = await EventBusTestHelpers.WaitForAsync(
                async () => JsonDocument.Parse(await coreHttpClient.GetStringAsync($"/api/v1/events/{deletedEventId}")),
                d => d.RootElement.GetProperty("deliveries").EnumerateArray().Count(x => x.GetProperty("status").GetString() == "delivered") == 2);
            var deliveries = detail.RootElement.GetProperty("deliveries").EnumerateArray().ToList();
            Assert.Equal(2, deliveries.Count);
            Assert.All(deliveries, d =>
            {
                Assert.Equal("demo.contacts|OnPartyDeleted", d.GetProperty("subscriberKey").GetString());
                Assert.Equal("delivered", d.GetProperty("status").GetString());
            });
            Assert.Single(deliveries, d => d.GetProperty("isReplay").GetBoolean());
        }
        finally
        {
            orchestrator.UnregisterEndpoint("demo.contacts", "1.0.0");
            await api.UninstallAppAsync("demo.contacts");
            await api.UninstallAppAsync("core.parties");
        }
    }

    [Fact]
    public async Task SynchronousHooks_EnforceDeadline_MutateOnBeforeSave_AndRequireValidShortLivedTokenOnGrpcCallbacks()
    {
        await using var coreFactory = CreateCoreFactory();
        using var coreHttpClient = coreFactory.CreateClient();

        await using var slowAndMutatingHost = await StartIsolatedAppContainerAsync<MutatingAndSlowTestApp>(
            () => coreFactory.Server.CreateHandler());

        var orchestrator = coreFactory.Services.GetRequiredService<KubernetesAppOrchestrator>();
        var hostOptions = coreFactory.Services.GetRequiredService<AppHostOptions>();
        var i18n = new I18nService();
        var api = new ErpApiClient(coreHttpClient, i18n);

        await api.DevLoginAsync();
        Assert.True(await api.InstallBuiltInAppAsync("core.parties"));
        Assert.True(await api.InstallBuiltInAppAsync("demo.contacts"));

        orchestrator.RegisterEndpoint(
            "demo.contacts",
            "1.0.0",
            "http://test-app-container:50051",
            () => slowAndMutatingHost.GetTestServer().CreateHandler());

        try
        {
            var party = await api.CreateRecordAsync("core.party", new Dictionary<string, object?>
            {
                ["partyNumber"] = "P-HOOK-200",
                ["name"] = "Hook Test AS",
                ["partyType"] = "organization",
                ["role"] = "customer",
                ["organizationNumber"] = "912345678"
            });

            // 1. OnBeforeSave mutates text field ("[AutoPrefix] ...") within the same transaction
            var note = await api.CreateRecordAsync("demo.contactnote", new Dictionary<string, object?>
            {
                ["party"] = party.Id,
                ["text"] = "Original tekst"
            });
            Assert.Equal("[AutoPrefix] Original tekst", I18nService.UnwrapDisplayString(note.Fields["text"]));

            // 2. Synchronous hook exceeding the deadline is timed out and aborts the transaction
            hostOptions.SynchronousHookTimeout = TimeSpan.FromMilliseconds(200);
            using var timeoutRes = await coreHttpClient.PostAsJsonAsync(
                $"/api/v1/{DemoTenantSeeder.DemoCompanyId}/demo.contactnote",
                new Dictionary<string, object?>
                {
                    ["party"] = party.Id,
                    ["text"] = "SLOW_HOOK"
                });

            Assert.Equal(HttpStatusCode.BadRequest, timeoutRes.StatusCode);
            var timeoutBody = await timeoutRes.Content.ReadAsStringAsync();
            Assert.Contains("deadline", timeoutBody, StringComparison.OrdinalIgnoreCase);

            // 3. Direct gRPC call to Core DataService without a valid short-lived token must fail with Unauthenticated
            using var unauthChannel = GrpcChannel.ForAddress(
                "http://localhost",
                new GrpcChannelOptions { HttpHandler = coreFactory.Server.CreateHandler() });
            var dataClient = new DataService.DataServiceClient(unauthChannel);

            var rpcEx = await Assert.ThrowsAsync<RpcException>(async () =>
                await dataClient.QueryAsync(new DataQueryRequest
                {
                    Context = new CallContextProto
                    {
                        TenantId = DemoTenantSeeder.DemoTenantId.ToString("D"),
                        AccessToken = "invalid-token"
                    },
                    ObjectId = "core.party"
                }));

            Assert.Equal(StatusCode.Unauthenticated, rpcEx.StatusCode);
        }
        finally
        {
            hostOptions.SynchronousHookTimeout = TimeSpan.FromSeconds(5);
            orchestrator.UnregisterEndpoint("demo.contacts", "1.0.0");
            await api.UninstallAppAsync("demo.contacts");
            await api.UninstallAppAsync("core.parties");
        }
    }

    private sealed class MutatingAndSlowTestApp : AppBase
    {
        [OnBeforeSave("demo.contactnote")]
        public async Task MutateBeforeSave(Erp.Sdk.Record note, Erp.Sdk.AppContext ctx, CancellationToken ct)
        {
            var text = note.Get<string>("text") ?? string.Empty;
            if (text == "SLOW_HOOK")
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }

            if (!text.StartsWith("[AutoPrefix] ", StringComparison.Ordinal))
            {
                note.Set("text", $"[AutoPrefix] {text}");
            }
        }
    }

    private static async Task<WebApplication> StartIsolatedAppContainerAsync<TApp>(
        Func<HttpMessageHandler> coreChannelHandlerFactory)
        where TApp : AppBase
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddErpApp<TApp>(opts =>
        {
            opts.CoreGrpcAddress = "http://erp-core-loopback";
            opts.ChannelHandlerFactory = coreChannelHandlerFactory;
        });

        var app = builder.Build();
        app.MapErpApp<TApp>();
        await app.StartAsync();
        return app;
    }

    private WebApplicationFactory<Program> CreateCoreFactory()
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

