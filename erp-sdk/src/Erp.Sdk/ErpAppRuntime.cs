using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Erp.Sdk.Contracts.V1;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Sdk;

public sealed class ErpAppRuntimeOptions
{
    /// <summary>
    /// gRPC base address of the Core platform for App -> Core callbacks.
    /// Can also be overridden per-call via the <c>x-erp-core-endpoint</c> gRPC header.
    /// </summary>
    public string CoreGrpcAddress { get; set; } = "http://localhost:5000";

    /// <summary>
    /// Optional custom <see cref="HttpMessageHandler"/> for creating the callback <see cref="GrpcChannel"/>
    /// (useful in loopback or integration test environments).
    /// </summary>
    public Func<HttpMessageHandler>? ChannelHandlerFactory { get; set; }
}

internal sealed class GrpcAppDataClient(DataService.DataServiceClient client, CallContextProto context) : IAppDataClient
{
    public async Task<Record?> GetAsync(string objectId, Guid id, CancellationToken cancellationToken = default)
    {
        var res = await client.GetAsync(
            new DataGetRequest
            {
                Context = context,
                ObjectId = objectId,
                RecordId = id.ToString("D")
            },
            CreateAuthHeaders(context),
            cancellationToken: cancellationToken);

        return res.Found && res.Record is not null ? FromProto(res.Record) : null;
    }

    public async Task<IReadOnlyList<Record>> QueryAsync(
        string objectId,
        string? filterExpression = null,
        string? orderBy = null,
        int top = 50,
        int skip = 0,
        CancellationToken cancellationToken = default)
    {
        var res = await client.QueryAsync(
            new DataQueryRequest
            {
                Context = context,
                ObjectId = objectId,
                FilterExpression = filterExpression ?? string.Empty,
                OrderBy = orderBy ?? string.Empty,
                Top = top,
                Skip = skip
            },
            CreateAuthHeaders(context),
            cancellationToken: cancellationToken);

        return res.Records.Select(FromProto).ToList();
    }

    public Task<IReadOnlyList<Record>> QueryAsync(
        string objectId,
        Func<RecordFilterBuilder, FilterPredicate> filter,
        string? orderBy = null,
        int top = 50,
        int skip = 0,
        CancellationToken cancellationToken = default)
    {
        var expr = filter(new RecordFilterBuilder()).Expression;
        return QueryAsync(objectId, expr, orderBy, top, skip, cancellationToken);
    }

    public async Task<Record> CreateAsync(
        string objectId,
        IDictionary<string, object?> fields,
        CancellationToken cancellationToken = default)
    {
        var res = await client.CreateAsync(
            new DataCreateRequest
            {
                Context = context,
                ObjectId = objectId,
                FieldsJson = JsonSerializer.Serialize(fields)
            },
            CreateAuthHeaders(context),
            cancellationToken: cancellationToken);

        return FromProto(res.Record);
    }

    public async Task<Record> UpdateAsync(
        string objectId,
        Guid id,
        long rowVersion,
        IDictionary<string, object?> fields,
        CancellationToken cancellationToken = default)
    {
        var res = await client.UpdateAsync(
            new DataUpdateRequest
            {
                Context = context,
                ObjectId = objectId,
                RecordId = id.ToString("D"),
                RowVersion = rowVersion,
                FieldsJson = JsonSerializer.Serialize(fields)
            },
            CreateAuthHeaders(context),
            cancellationToken: cancellationToken);

        return FromProto(res.Record);
    }

    public async Task<bool> DeleteAsync(
        string objectId,
        Guid id,
        long rowVersion,
        CancellationToken cancellationToken = default)
    {
        var res = await client.DeleteAsync(
            new DataDeleteRequest
            {
                Context = context,
                ObjectId = objectId,
                RecordId = id.ToString("D"),
                RowVersion = rowVersion
            },
            CreateAuthHeaders(context),
            cancellationToken: cancellationToken);

        return res.Deleted;
    }

    public Task<int> DeleteWhere(
        string objectId,
        Func<RecordFilterBuilder, FilterPredicate> filter,
        CancellationToken cancellationToken = default)
    {
        var expr = filter(new RecordFilterBuilder()).Expression;
        return DeleteWhere(objectId, expr, cancellationToken);
    }

    public async Task<int> DeleteWhere(
        string objectId,
        string filterExpression,
        CancellationToken cancellationToken = default)
    {
        var res = await client.DeleteWhereAsync(
            new DataDeleteWhereRequest
            {
                Context = context,
                ObjectId = objectId,
                FilterExpression = filterExpression
            },
            CreateAuthHeaders(context),
            cancellationToken: cancellationToken);

        return res.DeletedCount;
    }

    internal static Metadata CreateAuthHeaders(CallContextProto ctx)
    {
        var headers = new Metadata();
        if (!string.IsNullOrWhiteSpace(ctx.AccessToken))
        {
            headers.Add("authorization", $"Bearer {ctx.AccessToken}");
        }
        if (!string.IsNullOrWhiteSpace(ctx.CorrelationId))
        {
            headers.Add("x-correlation-id", ctx.CorrelationId);
        }
        return headers;
    }

    private static Record FromProto(DataRecordProto proto)
    {
        var fields = string.IsNullOrWhiteSpace(proto.FieldsJson)
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(proto.FieldsJson)
              ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        return new Record(
            id: Guid.TryParse(proto.Id, out var id) ? id : Guid.Empty,
            rowVersion: proto.RowVersion,
            fields: fields,
            tenantId: Guid.TryParse(proto.TenantId, out var tid) ? tid : Guid.Empty,
            companyId: Guid.TryParse(proto.CompanyId, out var cid) ? cid : null);
    }
}

internal sealed class GrpcAppEventClient(EventService.EventServiceClient client, CallContextProto context) : IAppEventClient
{
    public async Task<string> PublishAsync(
        string eventName,
        string aggregateId,
        object payload,
        CancellationToken cancellationToken = default)
    {
        var res = await client.PublishAsync(
            new PublishEventRequest
            {
                Context = context,
                EventName = eventName,
                AggregateId = aggregateId,
                PayloadJson = JsonSerializer.Serialize(payload)
            },
            GrpcAppDataClient.CreateAuthHeaders(context),
            cancellationToken: cancellationToken);

        return res.EventId;
    }
}

internal sealed class GrpcAppWorkflowClient(WorkflowService.WorkflowServiceClient client, CallContextProto context) : IAppWorkflowClient
{
    public async Task<string> StartWorkflowAsync(
        string workflowId,
        string objectId,
        Guid recordId,
        CancellationToken cancellationToken = default)
    {
        var res = await client.StartWorkflowAsync(
            new StartWorkflowRequest
            {
                Context = context,
                WorkflowId = workflowId,
                ObjectId = objectId,
                RecordId = recordId.ToString("D")
            },
            GrpcAppDataClient.CreateAuthHeaders(context),
            cancellationToken: cancellationToken);

        return res.WorkflowInstanceId;
    }
}

internal sealed class GrpcAppDocumentClient(DocumentService.DocumentServiceClient client, CallContextProto context) : IAppDocumentClient
{
    public async Task<Guid> StoreDocumentAsync(string fileName, string contentType, byte[] content, CancellationToken cancellationToken = default)
    {
        var res = await client.StoreDocumentAsync(
            new StoreDocumentRequest
            {
                Context = context,
                FileName = fileName,
                ContentType = contentType,
                Content = Google.Protobuf.ByteString.CopyFrom(content)
            },
            GrpcAppDataClient.CreateAuthHeaders(context),
            cancellationToken: cancellationToken);
        return Guid.Parse(res.FileId);
    }

    public async Task<AppDocumentFile?> GetDocumentAsync(Guid fileId, CancellationToken cancellationToken = default)
    {
        var res = await client.GetDocumentAsync(
            new GetDocumentRequest
            {
                Context = context,
                FileId = fileId.ToString("D")
            },
            GrpcAppDataClient.CreateAuthHeaders(context),
            cancellationToken: cancellationToken);

        if (!res.Found)
        {
            return null;
        }

        return new AppDocumentFile(
            res.FileName,
            res.ContentType,
            res.Content.ToByteArray(),
            res.Sha256);
    }

    public Task<AppInboxItem> ClaimInboxItemAsync(Guid itemId, string claimant, CancellationToken cancellationToken = default) =>
        InboxActionAsync(client.ClaimInboxItemAsync, itemId, claimant, null, null, null, cancellationToken);

    public Task<AppInboxItem> ReleaseInboxItemAsync(Guid itemId, string claimant, CancellationToken cancellationToken = default) =>
        InboxActionAsync(client.ReleaseInboxItemAsync, itemId, claimant, null, null, null, cancellationToken);

    public Task<AppInboxItem> CompleteInboxItemAsync(
        Guid itemId,
        string claimant,
        string? resultObjectId = null,
        Guid? resultRecordId = null,
        CancellationToken cancellationToken = default) =>
        InboxActionAsync(client.CompleteInboxItemAsync, itemId, claimant, resultObjectId, resultRecordId, null, cancellationToken);

    public Task<AppInboxItem> RejectInboxItemAsync(
        Guid itemId,
        string claimant,
        string? reason = null,
        CancellationToken cancellationToken = default) =>
        InboxActionAsync(client.RejectInboxItemAsync, itemId, claimant, null, null, reason, cancellationToken);

    private async Task<AppInboxItem> InboxActionAsync(
        Func<InboxItemActionRequest, Grpc.Core.Metadata, DateTime?, CancellationToken, Grpc.Core.AsyncUnaryCall<InboxItemProto>> call,
        Guid itemId,
        string claimant,
        string? resultObjectId,
        Guid? resultRecordId,
        string? reason,
        CancellationToken cancellationToken)
    {
        var res = await call(
            new InboxItemActionRequest
            {
                Context = context,
                ItemId = itemId.ToString("D"),
                Claimant = claimant,
                ResultObjectId = resultObjectId ?? string.Empty,
                ResultRecordId = resultRecordId?.ToString("D") ?? string.Empty,
                Reason = reason ?? string.Empty
            },
            GrpcAppDataClient.CreateAuthHeaders(context),
            null,
            cancellationToken);

        return new AppInboxItem(
            Guid.Parse(res.Id),
            Guid.Parse(res.InboxId),
            Guid.TryParse(res.CompanyId, out var companyId) ? companyId : null,
            res.Channel,
            res.Sender,
            res.Subject,
            string.IsNullOrEmpty(res.BodyText) ? null : res.BodyText,
            res.Status,
            res.FileIds.Select(Guid.Parse).ToList(),
            res.DuplicateFileIds.Select(Guid.Parse).ToList(),
            string.IsNullOrEmpty(res.ClaimedBy) ? null : res.ClaimedBy);
    }
}

/// <summary>
/// gRPC server hosted inside an App container (<see cref="AppService.AppServiceBase"/>)
/// that dispatches hooks, events, interface calls, and migrations to <typeparamref name="TApp"/>.
/// </summary>
public sealed class ErpAppGrpcService<TApp>(
    TApp appInstance,
    ErpAppRuntimeOptions runtimeOptions) : AppService.AppServiceBase
    where TApp : AppBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // Idempotency store tracking processed (tenantId, eventId, handlerName) keys
    private static readonly ConcurrentDictionary<string, byte> ProcessedEvents = new(StringComparer.OrdinalIgnoreCase);

    public override Task<HealthCheckResponse> HealthCheck(HealthCheckRequest request, ServerCallContext context)
    {
        var version = typeof(TApp).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        return Task.FromResult(new HealthCheckResponse
        {
            Healthy = true,
            Version = version
        });
    }

    public override async Task<HookResponse> InvokeHook(HookRequest request, ServerCallContext context)
    {
        var appCtx = BuildAppContext(request.Context, context);
        var fields = string.IsNullOrWhiteSpace(request.FieldsJson)
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(request.FieldsJson, JsonOptions)
              ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        var record = new Record(
            id: Guid.TryParse(request.RecordId, out var rid) ? rid : Guid.Empty,
            rowVersion: request.RowVersion <= 0 ? 1 : request.RowVersion,
            fields: fields,
            tenantId: appCtx.TenantId,
            companyId: appCtx.CompanyId);

        var methods = typeof(TApp).GetMethods(BindingFlags.Public | BindingFlags.Instance);
        var errors = new List<ValidationFailure>();

        foreach (var method in methods)
        {
            var matchesHook = request.HookType switch
            {
                HookTypeProto.OnValidate => method.GetCustomAttributes<OnValidateAttribute>()
                    .Any(a => string.Equals(a.ObjectId, request.ObjectId, StringComparison.OrdinalIgnoreCase)),
                HookTypeProto.OnBeforeSave => method.GetCustomAttributes<OnBeforeSaveAttribute>()
                    .Any(a => string.Equals(a.ObjectId, request.ObjectId, StringComparison.OrdinalIgnoreCase)),
                HookTypeProto.OnAfterSave => method.GetCustomAttributes<OnAfterSaveAttribute>()
                    .Any(a => string.Equals(a.ObjectId, request.ObjectId, StringComparison.OrdinalIgnoreCase)),
                _ => false
            };

            if (!matchesHook)
            {
                continue;
            }

            var args = BindHookParameters(method, record, appCtx, context.CancellationToken);
            var returnVal = method.Invoke(appInstance, args);

            if (returnVal is Task<ValidationResult> validationTask)
            {
                var validationResult = await validationTask.WaitAsync(context.CancellationToken);
                if (!validationResult.IsValid)
                {
                    errors.AddRange(validationResult.Failures);
                }
            }
            else if (returnVal is ValidationResult syncValidation)
            {
                if (!syncValidation.IsValid)
                {
                    errors.AddRange(syncValidation.Failures);
                }
            }
            else if (returnVal is Task task)
            {
                await task.WaitAsync(context.CancellationToken);
            }
        }

        var response = new HookResponse
        {
            IsValid = errors.Count == 0,
            ModifiedFieldsJson = record.IsModified
                ? JsonSerializer.Serialize(record.Fields, JsonOptions)
                : string.Empty
        };

        foreach (var err in errors)
        {
            response.Errors.Add(new ValidationErrorProto
            {
                Field = err.Field,
                Message = err.Message
            });
        }

        return response;
    }

    public override async Task<EventDeliveryResponse> DeliverEvent(EventDeliveryRequest request, ServerCallContext context)
    {
        var idempotencyKey = $"{request.Context?.TenantId}:{request.EventId}:{request.EventName}:{request.HandlerName}";
        if (!string.IsNullOrWhiteSpace(request.EventId) && !ProcessedEvents.TryAdd(idempotencyKey, 1))
        {
            return new EventDeliveryResponse
            {
                Acknowledged = true,
                AlreadyProcessed = true
            };
        }

        try
        {
            var appCtx = BuildAppContext(request.Context, context);
            var methods = typeof(TApp).GetMethods(BindingFlags.Public | BindingFlags.Instance);

            foreach (var method in methods)
            {
                var matchesAttr = method.GetCustomAttributes<OnEventAttribute>()
                    .Any(a => string.Equals(a.EventSpec, request.EventName, StringComparison.OrdinalIgnoreCase));
                var matchesName = !string.IsNullOrWhiteSpace(request.HandlerName)
                    && string.Equals(method.Name, request.HandlerName, StringComparison.OrdinalIgnoreCase);

                if (!matchesAttr && !matchesName)
                {
                    continue;
                }

                var args = BindEventParameters(method, request.PayloadJson, appCtx, context.CancellationToken);
                var result = method.Invoke(appInstance, args);
                if (result is Task task)
                {
                    await task.WaitAsync(context.CancellationToken);
                }
            }

            return new EventDeliveryResponse
            {
                Acknowledged = true,
                AlreadyProcessed = false
            };
        }
        catch
        {
            if (!string.IsNullOrWhiteSpace(request.EventId))
            {
                ProcessedEvents.TryRemove(idempotencyKey, out _);
            }
            throw;
        }
    }

    public override async Task<InterfaceCallResponse> InvokeInterface(InterfaceCallRequest request, ServerCallContext context)
    {
        var appCtx = BuildAppContext(request.Context, context);
        var method = typeof(TApp).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => string.Equals(m.Name, request.OperationName, StringComparison.OrdinalIgnoreCase))
            ?? throw new RpcException(new Status(StatusCode.Unimplemented, $"Interface operation '{request.OperationName}' not found on '{typeof(TApp).Name}'."));

        var args = BindEventParameters(method, request.InputJson, appCtx, context.CancellationToken);
        var returnObj = method.Invoke(appInstance, args);

        object? awaitedResult = null;
        if (returnObj is Task task)
        {
            await task.WaitAsync(context.CancellationToken);
            var taskType = task.GetType();
            if (taskType.IsGenericType)
            {
                awaitedResult = taskType.GetProperty("Result")?.GetValue(task);
            }
        }
        else
        {
            awaitedResult = returnObj;
        }

        return new InterfaceCallResponse
        {
            OutputJson = awaitedResult is null ? "{}" : JsonSerializer.Serialize(awaitedResult, JsonOptions)
        };
    }

    public override async Task<MigrationRunResponse> RunMigration(MigrationRunRequest request, ServerCallContext context)
    {
        var method = typeof(TApp).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.GetCustomAttributes<MigrationAttribute>()
                .Any(a => string.Equals(a.Name, request.MigrationClass, StringComparison.OrdinalIgnoreCase)))
            ?? throw new RpcException(new Status(
                StatusCode.NotFound,
                $"The app has no migration '{request.MigrationClass}'. Mark the method with [Migration(\"{request.MigrationClass}\")]."));

        var appCtx = BuildAppContext(request.Context, context);
        var state = new MigrationState(
            request.FromVersion,
            request.ToVersion,
            string.IsNullOrEmpty(request.Checkpoint) ? null : request.Checkpoint);

        var args = method.GetParameters().Select(p =>
            p.ParameterType == typeof(AppContext) ? (object?)appCtx
            : p.ParameterType == typeof(MigrationState) ? state
            : p.ParameterType == typeof(CancellationToken) ? context.CancellationToken
            : throw new RpcException(new Status(
                StatusCode.FailedPrecondition,
                $"Migration '{request.MigrationClass}' has an unsupported parameter '{p.Name}' of type {p.ParameterType.Name}."))).ToArray();

        MigrationStep step;
        try
        {
            var result = method.Invoke(appInstance, args);
            step = result switch
            {
                Task<MigrationStep> typed => await typed.WaitAsync(context.CancellationToken),
                MigrationStep direct => direct,
                Task plain => await CompleteAsync(plain, context.CancellationToken),
                _ => MigrationStep.Done()
            };
        }
        catch (Exception ex) when (ex is not RpcException and not OperationCanceledException)
        {
            // Carry the app's own error text back to the core, so a failed migration says why it failed.
            var cause = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            throw new RpcException(new Status(StatusCode.Internal, $"{cause.GetType().Name}: {cause.Message}"));
        }

        return new MigrationRunResponse
        {
            Completed = step.Completed,
            RecordsProcessed = step.RecordsProcessed,
            Checkpoint = step.Checkpoint ?? string.Empty
        };
    }

    public override async Task<CommandResponse> RunCommand(CommandRequest request, ServerCallContext context)
    {
        var method = typeof(TApp).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m => m.GetCustomAttributes<CommandAttribute>()
                .Any(a => string.Equals(a.Id, request.CommandId, StringComparison.OrdinalIgnoreCase)))
            ?? throw new RpcException(new Status(
                StatusCode.NotFound,
                $"The app has no handler for command '{request.CommandId}'. Mark the method with [Command(\"{request.CommandId}\")]."));

        var appCtx = BuildAppContext(request.Context, context);
        var command = new CommandContext
        {
            CommandId = request.CommandId,
            RunId = Guid.TryParse(request.RunId, out var runId) ? runId : Guid.Empty,
            DeviceId = request.DeviceId,
            Args = string.IsNullOrWhiteSpace(request.ArgsJson)
                ? default
                : System.Text.Json.JsonDocument.Parse(request.ArgsJson).RootElement.Clone(),
            Targets = request.Targets
                .Select(t => new CommandTarget(
                    t.ObjectId, Guid.TryParse(t.RecordId, out var id) ? id : Guid.Empty, t.BaseRowVersion, t.CurrentRowVersion, t.Exists))
                .ToList(),
            HasConflict = request.Conflict,
            Forced = request.Forced
        };

        var args = method.GetParameters().Select(p =>
            p.ParameterType == typeof(CommandContext) ? (object?)command
            : p.ParameterType == typeof(AppContext) ? appCtx
            : p.ParameterType == typeof(CancellationToken) ? context.CancellationToken
            : throw new RpcException(new Status(
                StatusCode.FailedPrecondition,
                $"Command '{request.CommandId}' has an unsupported parameter '{p.Name}' of type {p.ParameterType.Name}."))).ToArray();

        CommandResult result;
        try
        {
            var returned = method.Invoke(appInstance, args);
            result = returned switch
            {
                Task<CommandResult> typed => await typed.WaitAsync(context.CancellationToken),
                CommandResult direct => direct,
                Task plain => await CompleteCommandAsync(plain, context.CancellationToken),
                _ => CommandResult.Applied()
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var cause = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;

            // The core refused one of the handler's data calls (validation hook, permission, conflict, missing record):
            // that is the same answer an online user would get, so the command is rejected rather than failed.
            if (cause is RpcException { StatusCode: StatusCode.InvalidArgument or StatusCode.PermissionDenied or StatusCode.Aborted or StatusCode.NotFound } rpc)
            {
                return new CommandResponse { Outcome = "rejected", Message = rpc.Status.Detail };
            }

            throw new RpcException(new Status(StatusCode.Internal, $"{cause.GetType().Name}: {cause.Message}"));
        }

        return new CommandResponse
        {
            Outcome = result.Outcome,
            Message = result.Message ?? string.Empty,
            ResultJson = result.ResultJson ?? string.Empty
        };
    }

    private static async Task<CommandResult> CompleteCommandAsync(Task task, CancellationToken cancellationToken)
    {
        await task.WaitAsync(cancellationToken);
        return CommandResult.Applied();
    }

    /// <summary>A migration written as a plain <c>Task</c> has finished when the task has; errors propagate.</summary>
    private static async Task<MigrationStep> CompleteAsync(Task task, CancellationToken cancellationToken)
    {
        await task.WaitAsync(cancellationToken);
        return MigrationStep.Done();
    }

    private AppContext BuildAppContext(CallContextProto? proto, ServerCallContext callContext)
    {
        var ctxProto = proto ?? new CallContextProto();

        var endpointOverride = callContext.RequestHeaders.GetValue("x-erp-core-endpoint");
        var address = !string.IsNullOrWhiteSpace(endpointOverride)
            ? endpointOverride
            : runtimeOptions.CoreGrpcAddress;

        var channelOptions = new GrpcChannelOptions();
        if (runtimeOptions.ChannelHandlerFactory is not null)
        {
            channelOptions.HttpHandler = runtimeOptions.ChannelHandlerFactory();
        }

        var channel = GrpcChannel.ForAddress(address, channelOptions);

        var today = DateOnly.TryParse(ctxProto.TodayIso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedToday)
            ? parsedToday
            : DateOnly.FromDateTime(DateTime.UtcNow);

        return new AppContext
        {
            TenantId = Guid.TryParse(ctxProto.TenantId, out var tid) ? tid : Guid.Empty,
            CompanyId = Guid.TryParse(ctxProto.CompanyId, out var cid) ? cid : null,
            UserId = Guid.TryParse(ctxProto.UserId, out var uid) ? uid : Guid.Empty,
            CorrelationId = string.IsNullOrWhiteSpace(ctxProto.CorrelationId) ? Guid.NewGuid().ToString("D") : ctxProto.CorrelationId,
            AccessToken = ctxProto.AccessToken ?? string.Empty,
            Today = today,
            Data = new GrpcAppDataClient(new DataService.DataServiceClient(channel), ctxProto),
            Events = new GrpcAppEventClient(new EventService.EventServiceClient(channel), ctxProto),
            Workflows = new GrpcAppWorkflowClient(new WorkflowService.WorkflowServiceClient(channel), ctxProto),
            Documents = new GrpcAppDocumentClient(new DocumentService.DocumentServiceClient(channel), ctxProto)
        };
    }

    private static object?[] BindHookParameters(
        MethodInfo method,
        Record record,
        AppContext appCtx,
        CancellationToken cancellationToken)
    {
        var parameters = method.GetParameters();
        var args = new object?[parameters.Length];

        for (var i = 0; i < parameters.Length; i++)
        {
            var pType = parameters[i].ParameterType;
            if (pType == typeof(Record))
            {
                args[i] = record;
            }
            else if (pType == typeof(AppContext))
            {
                args[i] = appCtx;
            }
            else if (pType == typeof(CancellationToken))
            {
                args[i] = cancellationToken;
            }
        }

        return args;
    }

    private static object?[] BindEventParameters(
        MethodInfo method,
        string payloadJson,
        AppContext appCtx,
        CancellationToken cancellationToken)
    {
        var parameters = method.GetParameters();
        var args = new object?[parameters.Length];
        var safeJson = string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson;

        for (var i = 0; i < parameters.Length; i++)
        {
            var pType = parameters[i].ParameterType;
            if (pType == typeof(AppContext))
            {
                args[i] = appCtx;
            }
            else if (pType == typeof(CancellationToken))
            {
                args[i] = cancellationToken;
            }
            else if (pType == typeof(string))
            {
                args[i] = safeJson;
            }
            else
            {
                args[i] = JsonSerializer.Deserialize(safeJson, pType, JsonOptions);
            }
        }

        return args;
    }
}

public static class ErpAppHostExtensions
{
    public static IServiceCollection AddErpApp<TApp>(
        this IServiceCollection services,
        Action<ErpAppRuntimeOptions>? configure = null)
        where TApp : AppBase
    {
        var options = new ErpAppRuntimeOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        services.AddSingleton<TApp>();
        services.AddSingleton<ErpAppGrpcService<TApp>>();
        services.AddGrpc();
        return services;
    }

    public static IEndpointRouteBuilder MapErpApp<TApp>(this IEndpointRouteBuilder endpoints)
        where TApp : AppBase
    {
        endpoints.MapGrpcService<ErpAppGrpcService<TApp>>();
        return endpoints;
    }
}

