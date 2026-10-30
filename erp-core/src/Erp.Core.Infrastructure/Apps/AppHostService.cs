using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Erp.Core.Domain.Apps;
using Erp.Core.Domain.Data;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Persistence;
using Erp.Sdk.Contracts.V1;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.EntityFrameworkCore;

namespace Erp.Core.Infrastructure.Apps;

public sealed class AppHostOptions
{
    /// <summary>
    /// Strict synchronous hook timeout (default 5 seconds per Steg 6 specification).
    /// </summary>
    public TimeSpan SynchronousHookTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Deadline for one step (batch) of a data migration; much longer than a synchronous hook.</summary>
    public TimeSpan MigrationStepTimeout { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Deadline for an app to carry out one command queued by an offline client.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Optional Core callback endpoint URL sent via <c>x-erp-core-endpoint</c> header.
    /// </summary>
    public string? CoreCallbackEndpoint { get; set; }
}

/// <summary>
/// Invokes isolated App containers over gRPC for synchronous hooks (<c>OnValidate</c>,
/// <c>OnBeforeSave</c>, <c>OnAfterSave</c>) with a 5-second deadline, asynchronous domain events
/// (<c>OnEvent</c>), and cross-app contract interfaces (<c>Implements&lt;T&gt;</c>).
/// </summary>
public sealed class AppHostService(
    CoreDbContext dbContext,
    ITenantContext tenantContext,
    KubernetesAppOrchestrator orchestrator,
    IInternalTokenIssuer? tokenIssuer = null,
    AppHostOptions? options = null)
{
    private readonly AppHostOptions _options = options ?? new AppHostOptions();

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async Task InvokeSynchronousHooksAsync(
        HookTypeProto hookType,
        string objectId,
        string operation,
        Guid recordId,
        long rowVersion,
        IDictionary<string, object?> fields,
        Guid? companyId,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        var tenantId = tenantContext.TenantId;
        if (!tenantId.HasValue)
        {
            return;
        }

        var installedApps = await dbContext.InstalledApps
            .AsNoTracking()
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(cancellationToken);

        foreach (var app in installedApps)
        {
            var endpoint = orchestrator.ResolveEndpoint(app.AppId, app.Version);
            if (endpoint is null)
            {
                continue;
            }

            var callContext = CreateCallContextProto(tenantId.Value, companyId, correlationId);
            using var channel = CreateChannel(endpoint);
            var client = new AppService.AppServiceClient(channel);

            var headers = new Grpc.Core.Metadata();
            if (!string.IsNullOrWhiteSpace(_options.CoreCallbackEndpoint))
            {
                headers.Add("x-erp-core-endpoint", _options.CoreCallbackEndpoint);
            }

            var request = new HookRequest
            {
                Context = callContext,
                HookType = hookType,
                ObjectId = objectId,
                Operation = operation,
                RecordId = recordId.ToString("D"),
                RowVersion = rowVersion,
                FieldsJson = JsonSerializer.Serialize(fields, JsonOptions)
            };

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.SynchronousHookTimeout);

            HookResponse response;
            try
            {
                response = await client.InvokeHookAsync(
                    request,
                    headers: headers,
                    deadline: DateTime.UtcNow.Add(_options.SynchronousHookTimeout),
                    cancellationToken: timeoutCts.Token);
            }
            catch (RpcException ex) when (
                ex.StatusCode is StatusCode.DeadlineExceeded or StatusCode.Cancelled
                // The app's handler may surface our cancellation as Unknown ("Exception was thrown by handler").
                || (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested))
            {
                throw new RecordValidationException(
                    $"Synchronous hook '{hookType}' in app '{app.AppId}' exceeded the {_options.SynchronousHookTimeout.TotalSeconds:0.#}s deadline.");
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new RecordValidationException(
                    $"Synchronous hook '{hookType}' in app '{app.AppId}' exceeded the {_options.SynchronousHookTimeout.TotalSeconds:0.#}s deadline.");
            }

            if (!response.IsValid)
            {
                var firstErr = response.Errors.FirstOrDefault();
                var field = string.IsNullOrWhiteSpace(firstErr?.Field) ? null : firstErr.Field;
                var message = string.IsNullOrWhiteSpace(firstErr?.Message)
                    ? $"Validation rejected by app '{app.AppId}'."
                    : firstErr.Message;

                throw new RecordValidationException(message, field);
            }

            if (!string.IsNullOrWhiteSpace(response.ModifiedFieldsJson))
            {
                var modified = JsonSerializer.Deserialize<Dictionary<string, object?>>(response.ModifiedFieldsJson, JsonOptions);
                if (modified is not null)
                {
                    foreach (var (k, v) in modified)
                    {
                        fields[k] = v;
                    }
                }
            }
        }
    }

    /// <summary>
    /// Delivers one event to one handler of one app over gRPC. Throws when the app is unreachable or does not
    /// acknowledge, so that the event delivery worker can retry with backoff (ADR-0009). Must be called with the
    /// tenant (and user) context of the event active.
    /// </summary>
    public async Task DeliverEventToHandlerAsync(
        string appId,
        string handlerName,
        string eventId,
        string eventName,
        string payloadJson,
        Guid? companyId,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        var tenantId = tenantContext.TenantId
            ?? throw new InvalidOperationException("Cannot deliver an event without an active tenant context.");

        var app = await dbContext.InstalledApps
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.AppId == appId, cancellationToken)
            ?? throw new InvalidOperationException($"App '{appId}' is not installed in this tenant.");

        var endpoint = orchestrator.ResolveEndpoint(app.AppId, app.Version)
            ?? throw new InvalidOperationException($"No running container endpoint registered for app '{app.AppId}@{app.Version}'.");

        using var channel = CreateChannel(endpoint);
        var client = new AppService.AppServiceClient(channel);

        var headers = new Grpc.Core.Metadata();
        if (!string.IsNullOrWhiteSpace(_options.CoreCallbackEndpoint))
        {
            headers.Add("x-erp-core-endpoint", _options.CoreCallbackEndpoint);
        }

        var res = await client.DeliverEventAsync(
            new EventDeliveryRequest
            {
                Context = CreateCallContextProto(tenantId, companyId, correlationId),
                EventId = eventId,
                EventName = eventName,
                HandlerName = handlerName,
                PayloadJson = payloadJson
            },
            headers: headers,
            cancellationToken: cancellationToken);

        if (!res.Acknowledged)
        {
            throw new InvalidOperationException($"App '{appId}' did not acknowledge event '{eventName}' (handler '{handlerName}').");
        }
    }

    /// <summary>
    /// Runs one step of a data migration in the app container of <paramref name="version"/> (the version being upgraded
    /// to). The app answers with a checkpoint to continue from, or that it has finished (ADR-0013).
    /// </summary>
    public async Task<MigrationRunResponse> RunMigrationStepAsync(
        string appId,
        string version,
        string fromVersion,
        string toVersion,
        string migrationClass,
        string? checkpoint,
        CancellationToken cancellationToken = default)
    {
        var tenantId = tenantContext.TenantId
            ?? throw new InvalidOperationException("Cannot run a migration without an active tenant context.");

        var endpoint = orchestrator.ResolveEndpoint(appId, version)
            ?? throw new InvalidOperationException($"No running container endpoint registered for app '{appId}@{version}'.");

        using var channel = CreateChannel(endpoint);
        var client = new AppService.AppServiceClient(channel);

        var headers = new Grpc.Core.Metadata();
        if (!string.IsNullOrWhiteSpace(_options.CoreCallbackEndpoint))
        {
            headers.Add("x-erp-core-endpoint", _options.CoreCallbackEndpoint);
        }

        // The call token is short-lived but a step may run longer than that; callbacks need a fresh one per step.
        return await client.RunMigrationAsync(
            new MigrationRunRequest
            {
                Context = CreateCallContextProto(tenantId, tenantContext.CompanyId, Guid.NewGuid().ToString("D")),
                FromVersion = fromVersion,
                ToVersion = toVersion,
                MigrationClass = migrationClass,
                Checkpoint = checkpoint ?? string.Empty
            },
            headers: headers,
            deadline: DateTime.UtcNow.Add(_options.MigrationStepTimeout),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Asks an app to carry out a command a client queued while offline (ADR-0014). The handler works through the data
    /// API as the current user, so validation hooks and permissions apply as they would online.
    /// </summary>
    public async Task<CommandResponse> RunCommandAsync(
        string appId,
        string commandId,
        Guid runId,
        Guid deviceId,
        string argsJson,
        IEnumerable<CommandTargetProto> targets,
        bool conflict,
        bool forced,
        CancellationToken cancellationToken = default)
    {
        var tenantId = tenantContext.TenantId
            ?? throw new InvalidOperationException("Cannot run a command without an active tenant context.");

        var installedApp = await dbContext.InstalledApps
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.AppId == appId, cancellationToken)
            ?? throw new KeyNotFoundException($"App '{appId}' is not installed in this tenant.");

        var endpoint = orchestrator.ResolveEndpoint(installedApp.AppId, installedApp.Version)
            ?? throw new InvalidOperationException($"No running container endpoint registered for app '{appId}@{installedApp.Version}'.");

        using var channel = CreateChannel(endpoint);
        var client = new AppService.AppServiceClient(channel);

        var headers = new Grpc.Core.Metadata();
        if (!string.IsNullOrWhiteSpace(_options.CoreCallbackEndpoint))
        {
            headers.Add("x-erp-core-endpoint", _options.CoreCallbackEndpoint);
        }

        var request = new CommandRequest
        {
            Context = CreateCallContextProto(tenantId, tenantContext.CompanyId, runId.ToString("D")),
            CommandId = commandId,
            RunId = runId.ToString("D"),
            DeviceId = deviceId.ToString("D"),
            ArgsJson = argsJson,
            Conflict = conflict,
            Forced = forced
        };
        request.Targets.AddRange(targets);

        return await client.RunCommandAsync(
            request,
            headers: headers,
            deadline: DateTime.UtcNow.Add(_options.CommandTimeout),
            cancellationToken: cancellationToken);
    }

    public async Task<TOutput?> InvokeInterfaceAsync<TOutput>(
        string appId,
        string interfaceId,
        string operationName,
        object input,
        Guid? companyId = null,
        CancellationToken cancellationToken = default)
    {
        var tenantId = tenantContext.TenantId
            ?? throw new InvalidOperationException("Cannot invoke app interface without an active tenant context.");

        var installedApp = await dbContext.InstalledApps
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.AppId == appId, cancellationToken)
            ?? throw new KeyNotFoundException($"App '{appId}' is not installed in this tenant.");

        var endpoint = orchestrator.ResolveEndpoint(installedApp.AppId, installedApp.Version)
            ?? throw new InvalidOperationException($"No running container endpoint registered for app '{appId}@{installedApp.Version}'.");

        var callContext = CreateCallContextProto(tenantId, companyId, Guid.NewGuid().ToString("D"));
        using var channel = CreateChannel(endpoint);
        var client = new AppService.AppServiceClient(channel);

        var headers = new Grpc.Core.Metadata();
        if (!string.IsNullOrWhiteSpace(_options.CoreCallbackEndpoint))
        {
            headers.Add("x-erp-core-endpoint", _options.CoreCallbackEndpoint);
        }

        var response = await client.InvokeInterfaceAsync(
            new InterfaceCallRequest
            {
                Context = callContext,
                InterfaceId = interfaceId,
                OperationName = operationName,
                InputJson = JsonSerializer.Serialize(input, JsonOptions)
            },
            headers: headers,
            deadline: DateTime.UtcNow.Add(_options.SynchronousHookTimeout),
            cancellationToken: cancellationToken);

        return string.IsNullOrWhiteSpace(response.OutputJson)
            ? default
            : JsonSerializer.Deserialize<TOutput>(response.OutputJson, JsonOptions);
    }

    private CallContextProto CreateCallContextProto(Guid tenantId, Guid? companyId, string correlationId)
    {
        var userId = tenantContext.UserId ?? tenantId;
        var effectiveCompanyId = companyId ?? tenantContext.CompanyId;
        var accessToken = tokenIssuer?.IssueShortLivedCallToken(
            tenantId,
            userId,
            effectiveCompanyId,
            tenantContext.Roles,
            TimeSpan.FromMinutes(2)) ?? string.Empty;

        return new CallContextProto
        {
            TenantId = tenantId.ToString("D"),
            CompanyId = effectiveCompanyId?.ToString("D") ?? string.Empty,
            UserId = userId.ToString("D"),
            CorrelationId = correlationId,
            AccessToken = accessToken,
            TodayIso = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        };
    }

    private static GrpcChannel CreateChannel(AppGrpcEndpointRegistration endpoint)
    {
        var options = new GrpcChannelOptions();
        if (endpoint.ChannelHandlerFactory is not null)
        {
            options.HttpHandler = endpoint.ChannelHandlerFactory();
        }

        return GrpcChannel.ForAddress(endpoint.Address, options);
    }

    internal static bool MatchesEventSpec(EventSubscriptionDefinition sub, string eventSpec)
    {
        var fullSub = $"{sub.EventId}@{sub.Version}";
        return string.Equals(fullSub, eventSpec, StringComparison.OrdinalIgnoreCase)
            || string.Equals(sub.EventId, eventSpec, StringComparison.OrdinalIgnoreCase);
    }
}
