using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.Client.Models;
using Erp.Client.Services;
using Erp.Core.Domain.Entities;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Notifications;
using Erp.Core.Infrastructure.Persistence;
using Erp.Core.Infrastructure.Workflow;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Xunit;

namespace Erp.Core.Tests;

[Collection(PostgresDatabaseCollection.Name)]
public sealed partial class WorkflowTests(PostgresDatabaseFixture fixture) : IAsyncLifetime
{
    private readonly List<string> _createdSubjects = [];

    public Task InitializeAsync() => Task.CompletedTask;

    /// <summary>Other tests expect the demo tenant to have exactly its seeded user, so remove the users created here.</summary>
    public async Task DisposeAsync()
    {
        if (_createdSubjects.Count == 0)
        {
            return;
        }

        await using var migrator = new NpgsqlConnection(fixture.MigrationConnectionString);
        await migrator.OpenAsync();
        await using var cmd = new NpgsqlCommand("""DELETE FROM core."user" WHERE external_subject = ANY(@subjects);""", migrator);
        cmd.Parameters.AddWithValue("subjects", _createdSubjects.ToArray());
        await cmd.ExecuteNonQueryAsync();
    }

    private const string TestOidcSigningKey = "test-only-external-oidc-signing-key-at-least-32-bytes!";
    private const string TestOidcIssuer = "https://oidc.test.local/realms/erp-dev";
    private const string TestOidcAudience = "erp-web";
    private const string ExpenseObject = "demo.expense";

    private sealed class CapturingEmailSender : IEmailSender
    {
        public ConcurrentQueue<EmailMessage> Sent { get; } = new();

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
        {
            Sent.Enqueue(message);
            return Task.CompletedTask;
        }
    }

    private sealed record TestUser(Guid Id, string Subject, string Email);

    private sealed class Session(HttpClient http, ErpApiClient api)
    {
        public HttpClient Http { get; } = http;
        public ErpApiClient Api { get; } = api;
    }

    [Fact]
    public async Task TwoStepApproval_RunsEndToEnd_WithLockEmailLinkAttentionEventsAndAudit()
    {
        var emails = new CapturingEmailSender();
        await using var factory = CreateFactory(emails);
        var manager = await CreateUserAsync("manager", "demo.manager");
        var finance = await CreateUserAsync("finance", "demo.finance");

        var admin = await LoginAsync(factory, null);
        var managerSession = await LoginAsync(factory, manager.Subject);
        var financeSession = await LoginAsync(factory, finance.Subject);
        Assert.True(await admin.Api.InstallBuiltInAppAsync("demo.approvals"));

        try
        {
            // 1. An expense above the second step's limit gets both steps; submitting moves it to 'pending'
            var expense = await admin.Api.CreateRecordAsync(ExpenseObject, Fields("Reise til Bergen", 80_000m));
            var submitted = await SubmitAsync(admin, expense);
            Assert.Equal("pending", Status(submitted));

            using var instanceDoc = await GetJsonAsync(admin.Http, $"/api/v1/workflow-instances?objectId={ExpenseObject}&recordId={expense.Id}");
            Assert.Equal("running", instanceDoc.RootElement.GetProperty("status").GetString());
            var statuses = instanceDoc.RootElement.GetProperty("tasks").EnumerateArray()
                .OrderBy(t => t.GetProperty("stepIndex").GetInt32())
                .Select(t => t.GetProperty("status").GetString()).ToList();
            Assert.Equal(["open", "waiting"], statuses);

            // 2. The record is locked while the flow runs, except fields marked editableInWorkflow
            await Assert.ThrowsAnyAsync<Exception>(() => admin.Api.UpdateRecordAsync(
                ExpenseObject, expense.Id, submitted.RowVersion, new Dictionary<string, object?> { ["amount"] = 1m }));
            var commented = await admin.Api.UpdateRecordAsync(
                ExpenseObject, expense.Id, submitted.RowVersion, new Dictionary<string, object?> { ["comment"] = "Se vedlegg" });
            Assert.Equal("Se vedlegg", I18nService.UnwrapDisplayString(commented.Fields["comment"]));

            using var directApprove = await admin.Http.PostAsync(
                $"/api/v1/{DemoTenantSeeder.DemoCompanyId}/{ExpenseObject}/{expense.Id}/approve", null);
            Assert.Equal(HttpStatusCode.BadRequest, directApprove.StatusCode);

            // 3. Only the manager has a task; the start page shows "Til godkjenning" for them alone
            var managerTask = Assert.Single(await OpenTasksAsync(managerSession));
            Assert.StartsWith("Avdelingsleder:", managerTask.GetProperty("title").GetString());
            Assert.Empty(await OpenTasksAsync(financeSession));
            Assert.Empty(await OpenTasksAsync(admin));

            var managerAttention = await AttentionAsync(managerSession, "core.task.mine");
            Assert.Equal(1, managerAttention!.Value.GetProperty("count").GetInt32());
            Assert.Equal("Til godkjenning", managerAttention.Value.GetProperty("label").GetString());
            Assert.Null(await AttentionAsync(financeSession, "core.task.mine"));

            // 4. The assignee was e-mailed signed links
            var managerMail = Assert.Single(emails.Sent, m => m.To == manager.Email && m.Subject.Contains("Reise til Bergen"));
            Assert.Contains("/api/v1/task-response?token=", managerMail.TextBody);

            // 5. A user who is not the assignee cannot act on the task
            using var forbidden = await financeSession.Http.PostAsJsonAsync(
                $"/api/v1/tasks/{managerTask.GetProperty("id").GetGuid()}/approve", new { });
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

            // 6. Step 1 approved by the manager -> step 2 opens for finance, attention counts follow
            using var approve1 = await managerSession.Http.PostAsJsonAsync(
                $"/api/v1/tasks/{managerTask.GetProperty("id").GetGuid()}/approve", new { comment = "OK fra meg" });
            Assert.Equal(HttpStatusCode.OK, approve1.StatusCode);

            Assert.Empty(await OpenTasksAsync(managerSession));
            Assert.Null(await AttentionAsync(managerSession, "core.task.mine"));
            Assert.Single(await OpenTasksAsync(financeSession));
            Assert.Equal(1, (await AttentionAsync(financeSession, "core.task.mine"))!.Value.GetProperty("count").GetInt32());

            // 7. Step 2 approved from the e-mailed link (GET shows a form, POST performs the action)
            var financeMail = Assert.Single(emails.Sent, m => m.To == finance.Email);
            var approveLink = Regex.Match(financeMail.TextBody, @"Godkjenn: (\S+)").Groups[1].Value;
            using var preview = await admin.Http.GetAsync(approveLink);
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            Assert.Single(await OpenTasksAsync(financeSession)); // opening the link does not act

            var token = Uri.UnescapeDataString(approveLink[(approveLink.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
            using var respond = await admin.Http.PostAsync(
                "/api/v1/task-response",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token, ["comment"] = "Godkjent på e-post" }));
            Assert.Equal(HttpStatusCode.OK, respond.StatusCode);
            Assert.Contains("godkjent", await respond.Content.ReadAsStringAsync());

            using var tamperedPost = await admin.Http.PostAsync(
                "/api/v1/task-response",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token + "x" }));
            Assert.Equal(HttpStatusCode.BadRequest, tamperedPost.StatusCode);

            // 8. Flow completed: onComplete transition ran and released the lock
            var finalRecord = await admin.Api.GetRecordAsync(ExpenseObject, expense.Id);
            Assert.Equal("approved", Status(finalRecord!));

            using var finalInstance = await GetJsonAsync(admin.Http, $"/api/v1/workflow-instances?objectId={ExpenseObject}&recordId={expense.Id}");
            Assert.Equal("completed", finalInstance.RootElement.GetProperty("status").GetString());
            Assert.All(finalInstance.RootElement.GetProperty("tasks").EnumerateArray(), t => Assert.Equal("approved", t.GetProperty("status").GetString()));

            // 9. Events: started, one stepcompleted per step, completed
            foreach (var (name, expected) in new[]
                     {
                         ("core.workflow.started@1", 1), ("core.workflow.stepcompleted@1", 2), ("core.workflow.completed@1", 1)
                     })
            {
                using var events = await GetJsonAsync(admin.Http, $"/api/v1/events?name={Uri.EscapeDataString(name)}&top=200");
                Assert.Equal(
                    expected,
                    events.RootElement.EnumerateArray().Count(e => e.GetProperty("aggregateId").GetString() == expense.Id.ToString("D")));
            }

            // 10. Every step is in the audit trail with who, when and the comment
            var taskIds = finalInstance.RootElement.GetProperty("tasks").EnumerateArray().Select(t => t.GetProperty("id").GetGuid()).ToArray();
            await using var migrator = new NpgsqlConnection(fixture.MigrationConnectionString);
            await migrator.OpenAsync();
            await using var audit = new NpgsqlCommand(
                """
                SELECT count(*) FROM core.task_audit
                 WHERE record_id = ANY(@ids) AND operation = 'UPDATE' AND new_data ->> 'status' = 'approved'
                   AND new_data ->> 'completed_by' IS NOT NULL AND new_data ->> 'comment' IS NOT NULL;
                """,
                migrator);
            audit.Parameters.AddWithValue("ids", taskIds);
            Assert.Equal(2L, await audit.ExecuteScalarAsync());
        }
        finally
        {
            await admin.Api.UninstallAppAsync("demo.approvals");
        }
    }

    [Fact]
    public async Task Rejection_CancelsRemainingSteps_AndRunsOnReject_AndNoApplicableStepCompletesImmediately()
    {
        await using var factory = CreateFactory(new CapturingEmailSender());
        var manager = await CreateUserAsync("manager", "demo.manager");
        var admin = await LoginAsync(factory, null);
        var managerSession = await LoginAsync(factory, manager.Subject);
        Assert.True(await admin.Api.InstallBuiltInAppAsync("demo.approvals"));

        try
        {
            // 20 000: only step 1 applies (step 2 needs more than 50 000). Rejecting sends the expense back to draft.
            var expense = await admin.Api.CreateRecordAsync(ExpenseObject, Fields("Lunsj", 20_000m));
            await SubmitAsync(admin, expense);

            var task = Assert.Single(await OpenTasksAsync(managerSession));
            using var reject = await managerSession.Http.PostAsJsonAsync(
                $"/api/v1/tasks/{task.GetProperty("id").GetGuid()}/reject", new { comment = "For dyrt" });
            Assert.Equal(HttpStatusCode.OK, reject.StatusCode);

            Assert.Equal("draft", Status((await admin.Api.GetRecordAsync(ExpenseObject, expense.Id))!));
            using var instance = await GetJsonAsync(admin.Http, $"/api/v1/workflow-instances?objectId={ExpenseObject}&recordId={expense.Id}");
            Assert.Equal("rejected", instance.RootElement.GetProperty("status").GetString());
            Assert.Equal(1, instance.RootElement.GetProperty("tasks").GetArrayLength());

            // The record is editable again after rejection
            var reloaded = (await admin.Api.GetRecordAsync(ExpenseObject, expense.Id))!;
            await admin.Api.UpdateRecordAsync(
                ExpenseObject, expense.Id, reloaded.RowVersion, new Dictionary<string, object?> { ["amount"] = 15_000m });

            // Amount 0: no step's rule holds, so the flow completes at once and the expense is approved
            var free = await admin.Api.CreateRecordAsync(ExpenseObject, Fields("Gratis kaffe", 0m));
            var auto = await SubmitAsync(admin, free);
            Assert.Equal("approved", Status(auto));
        }
        finally
        {
            await admin.Api.UninstallAppAsync("demo.approvals");
        }
    }

    [Fact]
    public async Task Delegation_MovesTheTask_AndOverdueTasksEscalateToTheConfiguredRole()
    {
        await using var factory = CreateFactory(new CapturingEmailSender());
        var manager = await CreateUserAsync("manager", "demo.manager");
        var colleague = await CreateUserAsync("colleague", "demo.colleague");
        var finance = await CreateUserAsync("finance", "demo.finance");
        var admin = await LoginAsync(factory, null);
        var managerSession = await LoginAsync(factory, manager.Subject);
        var colleagueSession = await LoginAsync(factory, colleague.Subject);
        var financeSession = await LoginAsync(factory, finance.Subject);
        Assert.True(await admin.Api.InstallBuiltInAppAsync("demo.approvals"));

        try
        {
            var expense = await admin.Api.CreateRecordAsync(ExpenseObject, Fields("Konferanse", 10_000m));
            await SubmitAsync(admin, expense);
            var task = Assert.Single(await OpenTasksAsync(managerSession));
            var taskId = task.GetProperty("id").GetGuid();

            // Delegation (allowed by the workflow): the task leaves the manager and lands with the colleague
            using var delegated = await managerSession.Http.PostAsJsonAsync($"/api/v1/tasks/{taskId}/delegate", new { userId = colleague.Id });
            Assert.Equal(HttpStatusCode.OK, delegated.StatusCode);
            Assert.Empty(await OpenTasksAsync(managerSession));
            Assert.Single(await OpenTasksAsync(colleagueSession));

            // A task past its deadline is escalated to the role in 'deadline.escalateTo'
            await using (var migrator = new NpgsqlConnection(fixture.MigrationConnectionString))
            {
                await migrator.OpenAsync();
                await using var overdue = new NpgsqlCommand("UPDATE core.task SET due_at = now() - interval '1 hour' WHERE id = @id;", migrator);
                overdue.Parameters.AddWithValue("id", taskId);
                await overdue.ExecuteNonQueryAsync();
            }

            var worker = factory.Services.GetServices<IHostedService>().OfType<TaskDeadlineWorker>().Single();
            Assert.True(await worker.RunOnceAsync(CancellationToken.None) >= 1);

            var escalated = Assert.Single(await OpenTasksAsync(financeSession));
            Assert.Equal(taskId, escalated.GetProperty("id").GetGuid());
            Assert.True(escalated.GetProperty("escalated").GetBoolean());
        }
        finally
        {
            await admin.Api.UninstallAppAsync("demo.approvals");
        }
    }

    [Fact]
    public async Task TenantCanEditWorkflow_WhichSurvivesReinstall_AndInvalidDefinitionsAreRejected()
    {
        await using var factory = CreateFactory(new CapturingEmailSender());
        var manager = await CreateUserAsync("manager", "demo.manager");
        var admin = await LoginAsync(factory, null);
        var managerSession = await LoginAsync(factory, manager.Subject);
        Assert.True(await admin.Api.InstallBuiltInAppAsync("demo.approvals"));

        try
        {
            const string custom =
                """
                {
                  "object": "demo.expense",
                  "mode": "sequential",
                  "delegation": "none",
                  "onComplete": "approve",
                  "onReject": "reject",
                  "steps": [
                    { "name": "Kun leder", "assignee": { "role": "demo.manager" },
                      "when": { "and": [ { "field": "amount", "operator": "gt", "value": 100 },
                                         { "field": "amount", "operator": "lt", "value": 1000000 } ] } }
                  ]
                }
                """;

            using var forbidden = await managerSession.Http.PutAsync(
                "/api/v1/workflows/demo.expense.approval", new StringContent(custom, System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

            using var saved = await admin.Http.PutAsync(
                "/api/v1/workflows/demo.expense.approval", new StringContent(custom, System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);

            using var invalid = await admin.Http.PutAsync(
                "/api/v1/workflows/demo.expense.approval",
                new StringContent(custom.Replace("\"role\": \"demo.manager\"", "\"rule\": \"dimension-owner\""), System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Contains("not supported", await invalid.Content.ReadAsStringAsync());

            // Reinstalling the app must not overwrite the tenant's customization
            Assert.True(await admin.Api.UninstallAppAsync("demo.approvals"));
            using var gone = await admin.Http.GetAsync("/api/v1/workflows/demo.expense.approval");
            Assert.Equal(HttpStatusCode.OK, gone.StatusCode); // customized definitions stay when the app is removed
            Assert.True(await admin.Api.InstallBuiltInAppAsync("demo.approvals"));

            using var def = await GetJsonAsync(admin.Http, "/api/v1/workflows/demo.expense.approval");
            Assert.True(def.RootElement.GetProperty("isCustomized").GetBoolean());
            Assert.Equal("Kun leder", def.RootElement.GetProperty("definition").GetProperty("steps")[0].GetProperty("name").GetString());

            // The edited flow is the one that runs: 80 000 now gets a single step
            var expense = await admin.Api.CreateRecordAsync(ExpenseObject, Fields("Stor innkjøp", 80_000m));
            await SubmitAsync(admin, expense);
            using var instance = await GetJsonAsync(admin.Http, $"/api/v1/workflow-instances?objectId={ExpenseObject}&recordId={expense.Id}");
            Assert.Equal(1, instance.RootElement.GetProperty("tasks").GetArrayLength());
        }
        finally
        {
            await admin.Api.UninstallAppAsync("demo.approvals");
            await using var migrator = new NpgsqlConnection(fixture.MigrationConnectionString);
            await migrator.OpenAsync();
            await using var cleanup = new NpgsqlCommand("DELETE FROM core.workflow_definition WHERE workflow_id = 'demo.expense.approval';", migrator);
            await cleanup.ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public async Task DeclarativeAttentionItem_IsCounted_AndCanBeHiddenSnoozedAndRestored()
    {
        await using var factory = CreateFactory(new CapturingEmailSender());
        var admin = await LoginAsync(factory, null);
        Assert.True(await admin.Api.InstallBuiltInAppAsync("demo.approvals"));

        try
        {
            await admin.Api.CreateRecordAsync(ExpenseObject, Fields("Kladd 1", 100m));
            await admin.Api.CreateRecordAsync(ExpenseObject, Fields("Kladd 2", 200m));

            var item = await AttentionAsync(admin, "demo.approvals.drafts");
            Assert.NotNull(item);
            Assert.True(item.Value.GetProperty("count").GetInt32() >= 2);
            Assert.Equal("info", item.Value.GetProperty("severity").GetString());
            Assert.Equal("demo.expense-list", item.Value.GetProperty("page").GetString());
            Assert.False(item.Value.GetProperty("hidden").GetBoolean());

            using var hide = await admin.Http.PostAsync("/api/v1/attention/demo.approvals.drafts/hide", null);
            Assert.Equal(HttpStatusCode.NoContent, hide.StatusCode);
            Assert.True((await AttentionAsync(admin, "demo.approvals.drafts"))!.Value.GetProperty("hidden").GetBoolean());

            using var restore = await admin.Http.DeleteAsync("/api/v1/attention/demo.approvals.drafts/dismissal");
            Assert.Equal(HttpStatusCode.NoContent, restore.StatusCode);
            Assert.False((await AttentionAsync(admin, "demo.approvals.drafts"))!.Value.GetProperty("hidden").GetBoolean());

            using var snooze = await admin.Http.PostAsJsonAsync("/api/v1/attention/demo.approvals.drafts/snooze", new { hours = 2 });
            Assert.Equal(HttpStatusCode.NoContent, snooze.StatusCode);
            var snoozed = (await AttentionAsync(admin, "demo.approvals.drafts"))!.Value;
            Assert.True(snoozed.GetProperty("hidden").GetBoolean());
            Assert.NotEqual(JsonValueKind.Null, snoozed.GetProperty("snoozedUntil").ValueKind);

            await admin.Http.DeleteAsync("/api/v1/attention/demo.approvals.drafts/dismissal");
        }
        finally
        {
            await admin.Api.UninstallAppAsync("demo.approvals");
        }
    }

    [Fact]
    public void TaskLinks_AreRejectedWhenTamperedWithOrExpired()
    {
        var tokens = new TaskLinkTokens(new EmailOptions());
        var tenant = Guid.NewGuid();
        var task = Guid.NewGuid();
        var user = Guid.NewGuid();

        var token = tokens.Create(tenant, task, user, "approve");
        var payload = tokens.Verify(token);
        Assert.Equal(new[] { tenant, task, user }, new[] { payload!.TenantId, payload.TaskId, payload.UserId });
        Assert.Equal("approve", payload.Action);

        Assert.Null(tokens.Verify(token + "A"));
        Assert.Null(tokens.Verify("garbage"));
        Assert.Null(tokens.Verify(null));
        Assert.Null(new TaskLinkTokens(new EmailOptions { LinkSigningKey = "another-signing-key-that-is-long-enough-123456" }).Verify(token));
        Assert.Null(tokens.Verify(tokens.Sign(payload with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) })));
    }

    // ------------------------------------------------------------------ helpers

    private static Dictionary<string, object?> Fields(string title, decimal amount) =>
        new() { ["title"] = title, ["amount"] = amount };

    private static string Status(ClientRecordData record) =>
        I18nService.UnwrapDisplayString(record.Fields["status"]);

    private static async Task<ClientRecordData> SubmitAsync(Session session, ClientRecordData expense) =>
        await session.Api.ExecuteTransitionAsync(ExpenseObject, expense.Id, "submit", expense.RowVersion);

    private static async Task<JsonDocument> GetJsonAsync(HttpClient http, string url)
    {
        using var res = await http.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync());
    }

    private static async Task<List<JsonElement>> OpenTasksAsync(Session session)
    {
        using var doc = await GetJsonAsync(session.Http, "/api/v1/tasks?mine=true&status=open");
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    /// <summary>The attention item with the given id that is currently shown (count above zero), or null.</summary>
    private static async Task<JsonElement?> AttentionAsync(Session session, string id)
    {
        using var doc = await GetJsonAsync(session.Http, "/api/v1/attention");
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (item.GetProperty("id").GetString() == id)
            {
                return item.Clone();
            }
        }

        return null;
    }

    private static async Task<Session> LoginAsync(WebApplicationFactory<Program> factory, string? subject)
    {
        var http = factory.CreateClient();
        var api = new ErpApiClient(http, new I18nService());
        Assert.NotNull(await api.DevLoginAsync(subject));
        return new Session(http, api);
    }

    private async Task<TestUser> CreateUserAsync(string name, params string[] roles)
    {
        var unique = Guid.NewGuid().ToString("N")[..8];
        var user = new TestUser(Guid.NewGuid(), $"{name}-{unique}", $"{name}-{unique}@demohandel.no");

        var tenantContext = new TenantContext();
        tenantContext.Set(DemoTenantSeeder.DemoTenantId, DemoTenantSeeder.DemoUserId);
        await using var db = fixture.CreateAppDbContext(tenantContext);
        db.Users.Add(new User
        {
            Id = user.Id,
            TenantId = DemoTenantSeeder.DemoTenantId,
            CompanyId = DemoTenantSeeder.DemoCompanyId,
            ExternalSubject = user.Subject,
            Email = user.Email,
            DisplayName = name,
            Roles = [.. roles, "core.user"],
            AccessibleCompanyIds = [DemoTenantSeeder.DemoCompanyId],
            IsActive = true
        });
        await db.SaveChangesAsync();
        _createdSubjects.Add(user.Subject);
        return user;
    }

    /// <summary>
    /// Role-assigned tasks are visible to everyone holding the role, so tasks left by other tests in the shared
    /// database would leak into this one. Close them before the test starts.
    /// </summary>
    private async Task ResetWorkflowStateAsync()
    {
        await using var migrator = new NpgsqlConnection(fixture.MigrationConnectionString);
        await migrator.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            """
            UPDATE core.task SET status = 'cancelled' WHERE status IN ('open', 'waiting');
            UPDATE core.workflow_instance SET status = 'rejected', completed_at = now() WHERE status = 'running';
            """,
            migrator);
        await cmd.ExecuteNonQueryAsync();
    }

    private WebApplicationFactory<Program> CreateFactory(IEmailSender emailSender)
    {
        ResetWorkflowStateAsync().GetAwaiter().GetResult();
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
                    ["Email:PublicBaseUrl"] = "http://localhost"
                });
            });

            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IEmailSender>();
                services.AddSingleton(emailSender);
            });
        });
    }
}
