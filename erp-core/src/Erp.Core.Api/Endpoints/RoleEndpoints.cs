using Erp.Core.Domain.Data;
using Erp.Core.Infrastructure.Security;

namespace Erp.Core.Api.Endpoints;

public sealed record SetRolePermissionSetsRequest(IReadOnlyList<string>? PermissionSets);

/// <summary>Role administration: which permission sets (defined by installed apps) each role holds.</summary>
public static class RoleEndpoints
{
    public static void MapRoleEndpoints(this IEndpointRouteBuilder v1)
    {
        // What the current user may do, so clients can hide actions that would be refused.
        v1.MapGet("/permissions/me", async (PermissionService permissions, CancellationToken ct) =>
            Results.Ok(new { isAdmin = permissions.IsAdmin, grants = await permissions.GetEffectiveAsync(ct) }));

        var roles = v1.MapGroup("/roles").AddEndpointFilter(EventEndpoints.RequireTenantAdminAsync);

        roles.MapGet("/", async (PermissionService permissions, CancellationToken ct) =>
            Results.Ok(await permissions.ListRolesAsync(ct)));

        roles.MapPut("/{role}/permission-sets", async (
            string role,
            SetRolePermissionSetsRequest request,
            PermissionService permissions,
            CancellationToken ct) =>
        {
            try
            {
                await permissions.SetRolePermissionSetsAsync(role, request.PermissionSets ?? [], ct);
                return Results.NoContent();
            }
            catch (RecordValidationException ex)
            {
                return Results.BadRequest(new { error = ex.Message, field = ex.FieldName });
            }
        });
    }
}
