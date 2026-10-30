using Erp.Core.Domain.Data;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Erp.Core.Api.Grpc;

/// <summary>Reports a missing permission to an app as <c>PermissionDenied</c> instead of an unknown server error.</summary>
public sealed class PermissionExceptionInterceptor : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(request, context);
        }
        catch (PermissionDeniedException ex)
        {
            throw new RpcException(new Status(StatusCode.PermissionDenied, ex.Message));
        }
    }
}
