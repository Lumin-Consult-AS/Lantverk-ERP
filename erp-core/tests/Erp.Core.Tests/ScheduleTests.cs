using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Erp.Core.Infrastructure.Apps;
using Erp.Core.Infrastructure.Scheduling;
using Erp.Sdk;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Erp.Core.Tests;

/// <summary>A tiny app whose command counts how many times the core has called it.</summary>
public sealed class TickerApp : AppBase
{
    public static int Ticks;
    public static bool Fail;

    [Command("demo.ticker.tick")]
    public Task<CommandResult> Tick(CommandContext cmd, Erp.Sdk.AppContext ctx)
    {
        if (Fail)
        {
            return Task.FromResult(CommandResult.Rejected("Tjenesten er nede."));
        }

        Interlocked.Increment(ref Ticks);
        return Task.FromResult(CommandResult.Applied());
    }
}

/// <summary>Scheduled app commands (ADR-0025): daily and interval timetables, retry, run now, and manifest rules.</summary>
[Collection(PostgresDatabaseCollection.Name)]
public sealed class ScheduleTests(PostgresDatabaseFixture fixture)
{
    private const string TestOidcSigningKey = "test-only-external-oidc-signing-key-at-least-32-bytes!";
    private const string TestOidcIssuer = "https://oidc.test.local/realms/erp-dev";
    private const string TestOidcAudience = "erp-web";

    private const string TickerYaml = """
        id: demo.ticker
        version: 1.0.0
        name: Ticker
        runtime:
          image: ghcr.io/example/ticker:1.0.0
        commands:
          - id: demo.ticker.tick
            handler: Tick
        schedules:
          - id: demo.ticker.daily
            command: demo.ticker.tick
            at: "06:30"
          - id: demo.ticker.often
            command: demo.ticker.tick
            everyMinutes: 10
        """;

    [Fact]
    public async Task ScheduledCommands_RunWhenDue_AreRetriedWhenTheyFail_AndCanBeRunByAnAdministrator()
    {
        await using var factory = CreateFactory();
        await using var host = await StartTickerAsync(factory);
        using var client = await LoginAsync(factory);
        var worker = factory.Services.GetServices<IHostedService>().OfType<ScheduleWorker>().Single();
        TickerApp.Ticks = 0;
        TickerApp.Fail = false;

        try
        {
            await InstallAsync(client, TickerYaml);
            var t0 = new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero);

            // A new schedule is due at once: both run, and each moves to its next slot
            var first = await worker.RunOnceAsync(t0, default);
            Assert.Equal(2, first.Count(r => r.AppId == "demo.ticker" && r.Status == "ok"));
            Assert.Equal(2, TickerApp.Ticks);
            var listed = await ListAsync(client);
            Assert.Equal(DateTimeOffset.Parse("2026-11-03T06:30:00Z"), Next(listed, "demo.ticker.daily")); // tomorrow, at 06:30 UTC
            Assert.Equal(t0.AddMinutes(10), Next(listed, "demo.ticker.often"));
            Assert.Equal("ok", listed.Single(s => s.GetProperty("scheduleId").GetString() == "demo.ticker.daily").GetProperty("lastStatus").GetString());

            // Nothing is due just after
            Assert.Empty(await worker.RunOnceAsync(t0.AddMinutes(1), default));
            Assert.Equal(2, TickerApp.Ticks);

            // Eleven minutes on, only the interval schedule is due
            var later = await worker.RunOnceAsync(t0.AddMinutes(11), default);
            Assert.Equal(["demo.ticker.often"], later.Select(r => r.ScheduleId).ToArray());
            Assert.Equal(3, TickerApp.Ticks);

            // The next day both are due again (the daily one at 06:30, however late the worker looks)
            var nextDay = await worker.RunOnceAsync(new DateTimeOffset(2026, 11, 3, 7, 0, 0, TimeSpan.Zero), default);
            Assert.Equal(2, nextDay.Count);
            Assert.Equal(5, TickerApp.Ticks);

            // A failing command: the failure is recorded, the schedule is retried later, and comes right when the app does
            TickerApp.Fail = true;
            var t1 = new DateTimeOffset(2026, 11, 3, 8, 0, 0, TimeSpan.Zero);
            var failed = await worker.RunOnceAsync(t1, default);
            Assert.Equal("failed", Assert.Single(failed).Status);
            Assert.Contains("nede", failed[0].Message);
            var afterFailure = await ListAsync(client);
            Assert.Equal("failed", afterFailure.Single(s => s.GetProperty("scheduleId").GetString() == "demo.ticker.often").GetProperty("lastStatus").GetString());
            Assert.Equal(t1.AddMinutes(15), Next(afterFailure, "demo.ticker.often")); // retried after the retry delay, not at once

            Assert.Empty(await worker.RunOnceAsync(t1.AddMinutes(5), default));
            TickerApp.Fail = false;
            var retried = await worker.RunOnceAsync(t1.AddMinutes(16), default);
            Assert.Equal("ok", Assert.Single(retried).Status);

            // An administrator can run one now, whatever the timetable says; an unknown one is refused
            var before = TickerApp.Ticks;
            using (var runNow = await client.PostAsync("/api/v1/schedules/demo.ticker/demo.ticker.daily/run", null))
            {
                Assert.Equal(HttpStatusCode.OK, runNow.StatusCode);
                Assert.Equal("ok", JsonDocument.Parse(await runNow.Content.ReadAsStringAsync()).RootElement.GetProperty("status").GetString());
            }

            Assert.Equal(before + 1, TickerApp.Ticks);
            using (var unknown = await client.PostAsync("/api/v1/schedules/demo.ticker/nope/run", null))
            {
                Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            }
        }
        finally
        {
            await client.DeleteAsync("/api/v1/apps/demo.ticker");
        }

        // Uninstalled: the schedules are gone, and the worker has nothing to do
        Assert.Empty(await ListAsync(client));
        Assert.Empty(await worker.RunOnceAsync(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero), default));
    }

    [Fact]
    public async Task ManifestsWithInvalidSchedules_AreRefused()
    {
        await using var factory = CreateFactory();
        using var client = await LoginAsync(factory);

        const string head = "id: demo.sched{0}\nversion: 1.0.0\nname: Ugyldig\nruntime:\n  image: ghcr.io/example/s:1.0.0\ncommands:\n  - id: demo.sched{0}.run\n    handler: Run\nschedules:\n";
        foreach (var (label, yaml, expected) in new[]
                 {
                     ("a command the app does not declare",
                      string.Format(head, 1) + "  - id: demo.sched1.job\n    command: demo.sched1.other\n    at: \"06:00\"\n",
                      "does not declare"),
                     ("both at and everyMinutes",
                      string.Format(head, 2) + "  - id: demo.sched2.job\n    command: demo.sched2.run\n    at: \"06:00\"\n    everyMinutes: 10\n",
                      "exactly one"),
                     ("neither at nor everyMinutes",
                      string.Format(head, 3) + "  - id: demo.sched3.job\n    command: demo.sched3.run\n",
                      "exactly one"),
                     ("an invalid time",
                      string.Format(head, 4) + "  - id: demo.sched4.job\n    command: demo.sched4.run\n    at: \"25:99\"\n",
                      "invalid 'at'"),
                     ("an interval below the minimum",
                      string.Format(head, 5) + "  - id: demo.sched5.job\n    command: demo.sched5.run\n    everyMinutes: 2\n",
                      "minimum"),
                     ("another app's prefix",
                      string.Format(head, 6) + "  - id: other.app.job\n    command: demo.sched6.run\n    everyMinutes: 10\n",
                      "prefix")
                 })
        {
            using var res = await Post(client, yaml);
            Assert.True(res.StatusCode == HttpStatusCode.BadRequest, $"{label}: expected 400, got {(int)res.StatusCode}");
            Assert.Contains(expected, await res.Content.ReadAsStringAsync());
        }

        using var declarative = await Post(
            client, "id: demo.sched7\nversion: 1.0.0\nname: Ugyldig\ncommands:\n  - id: demo.sched7.run\n    handler: Run\nschedules:\n  - id: demo.sched7.job\n    command: demo.sched7.run\n    everyMinutes: 10\n");
        Assert.Equal(HttpStatusCode.BadRequest, declarative.StatusCode);
        Assert.Contains("declarative", await declarative.Content.ReadAsStringAsync());
    }

    private static DateTimeOffset Next(List<JsonElement> list, string scheduleId) =>
        list.Single(s => s.GetProperty("scheduleId").GetString() == scheduleId).GetProperty("nextRunAt").GetDateTimeOffset();

    private static async Task<List<JsonElement>> ListAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>("/api/v1/schedules")).EnumerateArray().Select(e => e.Clone()).ToList();

    private static async Task InstallAsync(HttpClient client, string yaml)
    {
        using var res = await Post(client, yaml);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
    }

    private static async Task<HttpResponseMessage> Post(HttpClient client, string yaml)
    {
        var content = new ByteArrayContent(AppPackageFormat.CreateSignedZipFromFiles(new Dictionary<string, string> { ["app.yaml"] = yaml }));
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        return await client.PostAsync("/api/v1/apps/install", content);
    }

    private static async Task<WebApplication> StartTickerAsync(WebApplicationFactory<Program> core)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddErpApp<TickerApp>(opts =>
        {
            opts.CoreGrpcAddress = "http://erp-core-loopback";
            opts.ChannelHandlerFactory = () => core.Server.CreateHandler();
        });

        var app = builder.Build();
        app.MapErpApp<TickerApp>();
        await app.StartAsync();
        core.Services.GetRequiredService<KubernetesAppOrchestrator>().RegisterEndpoint(
            "demo.ticker", "1.0.0", "http://demo-ticker:50051", () => app.GetTestServer().CreateHandler());
        return app;
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
                    ["Auth:OidcAudience"] = TestOidcAudience,
                    ["Schedules:PollInterval"] = "01:00:00"
                });
            });
        });
    }
}
