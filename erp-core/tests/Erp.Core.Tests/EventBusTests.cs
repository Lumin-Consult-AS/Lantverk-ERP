using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.Client.Services;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Events;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Erp.Core.Tests;

internal static class EventBusTestHelpers
{
    public static async Task<T> WaitForAsync<T>(Func<Task<T>> read, Func<T, bool> done, int timeoutSeconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        var value = await read();
        while (!done(value) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(100);
            value = await read();
        }

        return value;
    }
}

[Collection(PostgresDatabaseCollection.Name)]
public sealed class EventBusTests(PostgresDatabaseFixture fixture)
{
    private const string TestOidcSigningKey = "test-only-external-oidc-signing-key-at-least-32-bytes!";
    private const string TestOidcIssuer = "https://oidc.test.local/realms/erp-dev";
    private const string TestOidcAudience = "erp-web";
    private const string WebhookUrl = "https://hooks.example.test/erp";

    private sealed record ReceivedWebhook(string Body, string Signature, string Timestamp, string EventId, string EventName);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public ConcurrentQueue<ReceivedWebhook> Received { get; } = new();
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Received.Enqueue(new ReceivedWebhook(
                await request.Content!.ReadAsStringAsync(cancellationToken),
                request.Headers.GetValues(WebhookSigner.SignatureHeader).Single(),
                request.Headers.GetValues(WebhookSigner.TimestampHeader).Single(),
                request.Headers.GetValues(WebhookSigner.EventIdHeader).Single(),
                request.Headers.GetValues(WebhookSigner.EventNameHeader).Single()));
            return new HttpResponseMessage(Status);
        }
    }

    [Fact]
    public async Task Webhook_ReceivesSignedEventFromOutbox_IsVisibleInLog_AndCanBeReplayed()
    {
        var webhookHandler = new CapturingHandler();
        await using var factory = CreateFactory(webhookHandler);
        using var http = factory.CreateClient();
        var api = new ErpApiClient(http, new I18nService());
        await api.DevLoginAsync();
        Assert.True(await api.InstallBuiltInAppAsync("core.parties"));

        using var createHookRes = await http.PostAsJsonAsync("/api/v1/webhooks", new { url = WebhookUrl, eventPattern = "core.party.*" });
        Assert.Equal(HttpStatusCode.Created, createHookRes.StatusCode);
        var webhookId = JsonDocument.Parse(await createHookRes.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        try
        {
            var party = await api.CreateRecordAsync("core.party", new Dictionary<string, object?>
            {
                ["partyNumber"] = "P-EVT-1",
                ["name"] = "Eventfjell AS",
                ["partyType"] = "organization",
                ["role"] = "customer",
                ["organizationNumber"] = "911222333"
            });

            // 1. Delivered to the webhook with a valid HMAC signature
            await EventBusTestHelpers.WaitForAsync(() => Task.FromResult(webhookHandler.Received.Count), n => n >= 1);
            Assert.True(webhookHandler.Received.TryPeek(out var first));
            Assert.Equal("core.party.created@1", first.EventName);

            using var secretRes = await http.GetAsync("/api/v1/webhooks/secret");
            var secret = JsonDocument.Parse(await secretRes.Content.ReadAsStringAsync()).RootElement.GetProperty("secret").GetString()!;
            Assert.Equal(
                WebhookSigner.Sign(secret, long.Parse(first.Timestamp), first.Body),
                first.Signature);

            using var envelope = JsonDocument.Parse(first.Body);
            Assert.Equal(first.EventId, envelope.RootElement.GetProperty("id").GetString());
            Assert.Equal(DemoTenantSeeder.DemoTenantId.ToString("D"), envelope.RootElement.GetProperty("tenantId").GetString());
            Assert.Equal(party.Id.ToString("D"), envelope.RootElement.GetProperty("aggregateId").GetString());
            Assert.False(string.IsNullOrWhiteSpace(envelope.RootElement.GetProperty("correlationId").GetString()));
            Assert.Equal(
                "Eventfjell AS",
                envelope.RootElement.GetProperty("payload").GetProperty("fields").GetProperty("name").GetString());

            // 2. Visible in the event log, with a delivered delivery
            var eventId = Guid.Parse(first.EventId);
            var detail = await EventBusTestHelpers.WaitForAsync(
                async () => JsonDocument.Parse(await http.GetStringAsync($"/api/v1/events/{eventId}")),
                d => d.RootElement.GetProperty("deliveries").EnumerateArray().All(x => x.GetProperty("status").GetString() == "delivered"));
            var delivery = Assert.Single(detail.RootElement.GetProperty("deliveries").EnumerateArray());
            Assert.Equal("delivered", delivery.GetProperty("status").GetString());
            Assert.Equal(webhookId.ToString("D"), delivery.GetProperty("subscriberKey").GetString());

            using var listDoc = JsonDocument.Parse(await http.GetStringAsync("/api/v1/events?name=core.party.created@1"));
            Assert.Contains(listDoc.RootElement.EnumerateArray(), e => e.GetProperty("id").GetGuid() == eventId);

            // 3. Replay delivers the same event again to the chosen webhook
            using var replayRes = await http.PostAsJsonAsync($"/api/v1/events/{eventId}/replay", new { webhook = webhookId });
            Assert.Equal(HttpStatusCode.Accepted, replayRes.StatusCode);
            await EventBusTestHelpers.WaitForAsync(() => Task.FromResult(webhookHandler.Received.Count), n => n >= 2);
            Assert.Equal(2, webhookHandler.Received.Count);
            Assert.All(webhookHandler.Received, r => Assert.Equal(first.EventId, r.EventId));

            // 4. Events are tenant-isolated: another tenant sees none of them
            await using var conn = new NpgsqlConnection(fixture.ApplicationConnectionString);
            await conn.OpenAsync();
            await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false);", conn))
            {
                set.Parameters.AddWithValue("t", Guid.NewGuid().ToString("D"));
                await set.ExecuteNonQueryAsync();
            }

            await using var count = new NpgsqlCommand("SELECT (SELECT count(*) FROM core.event_log) + (SELECT count(*) FROM core.event_delivery);", conn);
            Assert.Equal(0L, await count.ExecuteScalarAsync());
        }
        finally
        {
            await http.DeleteAsync($"/api/v1/webhooks/{webhookId}");
            await api.UninstallAppAsync("core.parties");
        }
    }

    [Fact]
    public async Task FailingWebhook_IsRetriedThenDeadLettered_WithoutAffectingThePublishingTransaction()
    {
        var webhookHandler = new CapturingHandler { Status = HttpStatusCode.InternalServerError };
        await using var factory = CreateFactory(webhookHandler);
        var options = factory.Services.GetRequiredService<EventBusOptions>();
        options.MaxAttempts = 3;
        options.BaseBackoff = TimeSpan.FromMilliseconds(50);

        using var http = factory.CreateClient();
        var api = new ErpApiClient(http, new I18nService());
        await api.DevLoginAsync();
        Assert.True(await api.InstallBuiltInAppAsync("core.parties"));

        using var createHookRes = await http.PostAsJsonAsync("/api/v1/webhooks", new { url = WebhookUrl });
        var webhookId = JsonDocument.Parse(await createHookRes.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();

        try
        {
            // The publishing transaction succeeds even though the subscriber fails (events never reject transactions)
            var party = await api.CreateRecordAsync("core.party", new Dictionary<string, object?>
            {
                ["partyNumber"] = "P-EVT-2",
                ["name"] = "Feilfjell AS",
                ["partyType"] = "organization",
                ["role"] = "customer",
                ["organizationNumber"] = "911222444"
            });
            Assert.NotEqual(Guid.Empty, party.Id);

            var dead = await EventBusTestHelpers.WaitForAsync(
                async () => JsonDocument.Parse(await http.GetStringAsync("/api/v1/events/dead-letters")),
                d => d.RootElement.GetArrayLength() > 0);

            var entry = Assert.Single(dead.RootElement.EnumerateArray(), e => e.GetProperty("subscriberKey").GetString() == webhookId.ToString("D"));
            Assert.Equal("dead", entry.GetProperty("status").GetString());
            Assert.Equal(3, entry.GetProperty("attempts").GetInt32());
            Assert.Contains("500", entry.GetProperty("lastError").GetString());
            Assert.Equal(3, webhookHandler.Received.Count);
        }
        finally
        {
            await http.DeleteAsync($"/api/v1/webhooks/{webhookId}");
            await api.UninstallAppAsync("core.parties");
        }
    }

    [Fact]
    public async Task RolledBackTransaction_PublishesNoEvent()
    {
        await using var factory = CreateFactory(new CapturingHandler());
        using var scope = factory.Services.CreateScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        tenant.Set(DemoTenantSeeder.DemoTenantId);
        var publisher = scope.ServiceProvider.GetRequiredService<EventPublisher>();

        await using var conn = new NpgsqlConnection(fixture.ApplicationConnectionString);
        await conn.OpenAsync();
        await using (var set = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false);", conn))
        {
            set.Parameters.AddWithValue("t", DemoTenantSeeder.DemoTenantId.ToString("D"));
            await set.ExecuteNonQueryAsync();
        }

        Guid rolledBackId;
        await using (var tx = await conn.BeginTransactionAsync())
        {
            rolledBackId = await publisher.PublishAsync(
                conn, tx, "demo.rollback.happened@1", "agg-1", new { ok = true }, null, null, CancellationToken.None);
            await tx.RollbackAsync();
        }

        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM core.event_log WHERE id = @id;", conn);
        cmd.Parameters.AddWithValue("id", rolledBackId);
        Assert.Equal(0L, await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task PurgeExpiredEvents_RemovesEventsOlderThanRetention_AndKeepsRecentOnes()
    {
        var oldId = Guid.NewGuid();
        var recentId = Guid.NewGuid();

        await using var migrator = new NpgsqlConnection(fixture.MigrationConnectionString);
        await migrator.OpenAsync();
        foreach (var (id, age) in new[] { (oldId, 100), (recentId, 10) })
        {
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO core.event_log (id, tenant_id, created_by, updated_by, event_name, aggregate_id, correlation_id, occurred_at, payload)
                VALUES (@id, @t, @t, @t, 'demo.purge.test@1', 'agg', 'corr', now() - make_interval(days => @age), '{}'::jsonb);
                """,
                migrator);
            insert.Parameters.AddWithValue("id", id);
            insert.Parameters.AddWithValue("t", DemoTenantSeeder.DemoTenantId);
            insert.Parameters.AddWithValue("age", age);
            await insert.ExecuteNonQueryAsync();
        }

        // erp_app has no DELETE on the log; retention is enforced by the SECURITY DEFINER function
        await using var app = new NpgsqlConnection(fixture.ApplicationConnectionString);
        await app.OpenAsync();
        await using (var directDelete = new NpgsqlCommand("DELETE FROM core.event_log WHERE id = @id;", app))
        {
            directDelete.Parameters.AddWithValue("id", recentId);
            await Assert.ThrowsAsync<PostgresException>(() => directDelete.ExecuteNonQueryAsync());
        }

        await using (var purge = new NpgsqlCommand("SELECT core.purge_expired_events(interval '90 days');", app))
        {
            Assert.True(Convert.ToInt32(await purge.ExecuteScalarAsync()) >= 1);
        }

        await using var exists = new NpgsqlCommand("SELECT id FROM core.event_log WHERE id = ANY(@ids);", migrator);
        exists.Parameters.AddWithValue("ids", new[] { oldId, recentId });
        await using var reader = await exists.ExecuteReaderAsync();
        var remaining = new List<Guid>();
        while (await reader.ReadAsync())
        {
            remaining.Add(reader.GetGuid(0));
        }

        Assert.Equal([recentId], remaining);
    }

    [Fact]
    public async Task EventAdministration_RequiresTenantAdminRole()
    {
        await using var factory = CreateFactory(new CapturingHandler());
        using var http = factory.CreateClient();

        using var anonymous = await http.GetAsync("/api/v1/events");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    private WebApplicationFactory<Program> CreateFactory(HttpMessageHandler webhookHandler)
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

            builder.ConfigureTestServices(services =>
                services.AddHttpClient(EventDeliveryWorker.WebhookHttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => webhookHandler));
        });
    }
}
