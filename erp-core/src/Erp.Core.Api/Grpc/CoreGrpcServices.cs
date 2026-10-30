using System.Text.Json;
using Erp.Core.Domain.Data;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Apps;
using Erp.Core.Infrastructure.Data;
using Erp.Core.Infrastructure.Events;
using Erp.Sdk.Contracts.V1;
using Google.Protobuf;
using Grpc.Core;

namespace Erp.Core.Api.Grpc;

internal static class GrpcCallContextHelper
{
    public static IDisposable AuthenticateAndBeginScope(
        CallContextProto? callContext,
        ServerCallContext grpcContext,
        IInternalTokenIssuer tokenIssuer,
        ITenantContext tenantContext,
        ActiveCallTransactionRegistry txRegistry,
        out Guid? companyId)
    {
        var token = callContext?.AccessToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            var authHeader = grpcContext.RequestHeaders.GetValue("authorization");
            if (!string.IsNullOrWhiteSpace(authHeader)
                && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = authHeader["Bearer ".Length..].Trim();
            }
        }

        var validated = tokenIssuer.ValidateToken(token ?? string.Empty)
            ?? throw new RpcException(new Status(
                StatusCode.Unauthenticated,
                "Missing or invalid short-lived internal platform token on callback to Core."));

        if (!string.IsNullOrWhiteSpace(callContext?.TenantId)
            && Guid.TryParse(callContext.TenantId, out var requestedTenantId)
            && requestedTenantId != validated.TenantId)
        {
            throw new RpcException(new Status(
                StatusCode.PermissionDenied,
                "CallContext tenant_id does not match the authenticated token tenant_id."));
        }

        companyId = Guid.TryParse(callContext?.CompanyId, out var cid)
            ? cid
            : validated.CompanyId;

        var correlationId = !string.IsNullOrWhiteSpace(callContext?.CorrelationId)
            ? callContext.CorrelationId
            : grpcContext.RequestHeaders.GetValue("x-correlation-id");

        var prevCorrelation = txRegistry.CurrentCorrelationId;
        txRegistry.CurrentCorrelationId = correlationId;
        var tenantScope = tenantContext.BeginScope(validated.TenantId, validated.UserId, companyId, validated.Roles);

        return new CombinedCallScope(tenantScope, txRegistry, prevCorrelation);
    }

    public static DataRecordProto ToProto(RecordData record)
    {
        return new DataRecordProto
        {
            Id = record.Id.ToString("D"),
            TenantId = record.TenantId.ToString("D"),
            CompanyId = record.CompanyId?.ToString("D") ?? string.Empty,
            RowVersion = record.RowVersion,
            FieldsJson = JsonSerializer.Serialize(record.Fields)
        };
    }

    private sealed class CombinedCallScope(
        IDisposable tenantScope,
        ActiveCallTransactionRegistry txRegistry,
        string? previousCorrelationId) : IDisposable
    {
        public void Dispose()
        {
            txRegistry.CurrentCorrelationId = previousCorrelationId;
            tenantScope.Dispose();
        }
    }
}

public sealed class CoreDataGrpcService(
    RecordDataService recordDataService,
    IInternalTokenIssuer tokenIssuer,
    ITenantContext tenantContext,
    ActiveCallTransactionRegistry txRegistry) : DataService.DataServiceBase
{
    public override async Task<DataRecordResponse> Get(DataGetRequest request, ServerCallContext context)
    {
        using var scope = GrpcCallContextHelper.AuthenticateAndBeginScope(
            request.Context,
            context,
            tokenIssuer,
            tenantContext,
            txRegistry,
            out var companyId);

        if (!Guid.TryParse(request.RecordId, out var recordId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid record_id UUID."));
        }

        var record = await recordDataService.GetAsync(request.ObjectId, recordId, companyId, context.CancellationToken);
        if (record is null)
        {
            return new DataRecordResponse { Found = false };
        }

        return new DataRecordResponse
        {
            Found = true,
            Record = GrpcCallContextHelper.ToProto(record)
        };
    }

    public override async Task<DataQueryResponse> Query(DataQueryRequest request, ServerCallContext context)
    {
        using var scope = GrpcCallContextHelper.AuthenticateAndBeginScope(
            request.Context,
            context,
            tokenIssuer,
            tenantContext,
            txRegistry,
            out var companyId);

        var filterNode = string.IsNullOrWhiteSpace(request.FilterExpression)
            ? null
            : FilterExpressionParser.Parse(request.FilterExpression);

        var records = await recordDataService.QueryAsync(
            request.ObjectId,
            new DataQuery
            {
                Filter = filterNode,
                Top = request.Top <= 0 ? 50 : request.Top,
                Skip = Math.Max(0, request.Skip)
            },
            companyId,
            context.CancellationToken);

        var response = new DataQueryResponse();
        foreach (var rec in records)
        {
            response.Records.Add(GrpcCallContextHelper.ToProto(rec));
        }

        return response;
    }

    public override async Task<DataRecordResponse> Create(DataCreateRequest request, ServerCallContext context)
    {
        using var scope = GrpcCallContextHelper.AuthenticateAndBeginScope(
            request.Context,
            context,
            tokenIssuer,
            tenantContext,
            txRegistry,
            out var companyId);

        var fields = string.IsNullOrWhiteSpace(request.FieldsJson)
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(request.FieldsJson)
              ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var created = await recordDataService.CreateAsync(
                request.ObjectId,
                fields,
                companyId,
                cancellationToken: context.CancellationToken);

            return new DataRecordResponse
            {
                Found = true,
                Record = GrpcCallContextHelper.ToProto(created)
            };
        }
        catch (RecordValidationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
    }

    public override async Task<DataRecordResponse> Update(DataUpdateRequest request, ServerCallContext context)
    {
        using var scope = GrpcCallContextHelper.AuthenticateAndBeginScope(
            request.Context,
            context,
            tokenIssuer,
            tenantContext,
            txRegistry,
            out var companyId);

        if (!Guid.TryParse(request.RecordId, out var recordId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid record_id UUID."));
        }

        var fields = string.IsNullOrWhiteSpace(request.FieldsJson)
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : JsonSerializer.Deserialize<Dictionary<string, object?>>(request.FieldsJson)
              ?? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var updated = await recordDataService.UpdateAsync(
                request.ObjectId,
                recordId,
                request.RowVersion,
                fields,
                companyId,
                context.CancellationToken);

            return new DataRecordResponse
            {
                Found = true,
                Record = GrpcCallContextHelper.ToProto(updated)
            };
        }
        catch (RecordValidationException ex)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (ConcurrencyConflictException ex)
        {
            throw new RpcException(new Status(StatusCode.Aborted, ex.Message));
        }
    }

    public override async Task<DataDeleteResponse> Delete(DataDeleteRequest request, ServerCallContext context)
    {
        using var scope = GrpcCallContextHelper.AuthenticateAndBeginScope(
            request.Context,
            context,
            tokenIssuer,
            tenantContext,
            txRegistry,
            out var companyId);

        if (!Guid.TryParse(request.RecordId, out var recordId))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Invalid record_id UUID."));
        }

        await recordDataService.DeleteAsync(
            request.ObjectId,
            recordId,
            request.RowVersion,
            companyId,
            context.CancellationToken);

        return new DataDeleteResponse { Deleted = true };
    }

    public override async Task<DataDeleteWhereResponse> DeleteWhere(DataDeleteWhereRequest request, ServerCallContext context)
    {
        using var scope = GrpcCallContextHelper.AuthenticateAndBeginScope(
            request.Context,
            context,
            tokenIssuer,
            tenantContext,
            txRegistry,
            out var companyId);

        var deletedCount = await recordDataService.DeleteWhereAsync(
            request.ObjectId,
            request.FilterExpression,
            companyId,
            context.CancellationToken);

        return new DataDeleteWhereResponse
        {
            DeletedCount = deletedCount
        };
    }
}

public sealed class CoreEventGrpcService(
    EventPublisher eventPublisher,
    IInternalTokenIssuer tokenIssuer,
    ITenantContext tenantContext,
    ActiveCallTransactionRegistry txRegistry) : EventService.EventServiceBase
{
    public override async Task<PublishEventResponse> Publish(PublishEventRequest request, ServerCallContext context)
    {
        using var scope = GrpcCallContextHelper.AuthenticateAndBeginScope(
            request.Context,
            context,
            tokenIssuer,
            tenantContext,
            txRegistry,
            out var companyId);

        // Apps publish through the same transactional outbox as the core (ADR-0009).
        var eventId = await eventPublisher.PublishInOwnTransactionAsync(
            request.EventName,
            string.IsNullOrWhiteSpace(request.AggregateId) ? request.EventName : request.AggregateId,
            request.PayloadJson,
            companyId,
            request.Context?.CorrelationId,
            context.CancellationToken);

        return new PublishEventResponse { EventId = eventId.ToString("D") };
    }
}

public sealed class CoreWorkflowGrpcService(
    IInternalTokenIssuer tokenIssuer,
    ITenantContext tenantContext,
    ActiveCallTransactionRegistry txRegistry) : WorkflowService.WorkflowServiceBase
{
    public override Task<StartWorkflowResponse> StartWorkflow(StartWorkflowRequest request, ServerCallContext context)
    {
        using var scope = GrpcCallContextHelper.AuthenticateAndBeginScope(
            request.Context,
            context,
            tokenIssuer,
            tenantContext,
            txRegistry,
            out _);

        return Task.FromResult(new StartWorkflowResponse
        {
            WorkflowInstanceId = Guid.NewGuid().ToString("D")
        });
    }
}

public sealed class CoreDocumentGrpcService(
    IInternalTokenIssuer tokenIssuer,
    ITenantContext tenantContext,
    ActiveCallTransactionRegistry txRegistry) : DocumentService.DocumentServiceBase
{
    public override Task<GetDocumentResponse> GetDocument(GetDocumentRequest request, ServerCallContext context)
    {
        using var scope = GrpcCallContextHelper.AuthenticateAndBeginScope(
            request.Context,
            context,
            tokenIssuer,
            tenantContext,
            txRegistry,
            out _);

        return Task.FromResult(new GetDocumentResponse
        {
            Found = false,
            Content = ByteString.Empty
        });
    }
}
