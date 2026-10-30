using System.Security.Claims;
using Erp.Core.Domain.Tenancy;

namespace Erp.Core.Api.Middleware;

/// <summary>
/// Populates the request-scoped <see cref="ITenantContext"/> from a validated internal JWT.
/// </summary>
public sealed class TenantContextMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ITenantContext tenantContext)
    {
        try
        {
            if (context.User.Identity?.IsAuthenticated == true
                && string.Equals(context.User.FindFirstValue("token_use"), "internal", StringComparison.Ordinal))
            {
                var tenantIdStr = context.User.FindFirstValue("tenant_id");
                var userIdStr = context.User.FindFirstValue("sub")
                    ?? context.User.FindFirstValue(ClaimTypes.NameIdentifier);
                var companyIdStr = context.User.FindFirstValue("company_id");

                if (Guid.TryParse(tenantIdStr, out var tenantId) && Guid.TryParse(userIdStr, out var userId))
                {
                    Guid? companyId = Guid.TryParse(companyIdStr, out var parsedCompanyId)
                        ? parsedCompanyId
                        : null;

                    var roles = context.User.FindAll(ClaimTypes.Role)
                        .Select(c => c.Value)
                        .Concat(context.User.FindAll("role").Select(c => c.Value))
                        .Distinct(StringComparer.Ordinal)
                        .ToList();

                    tenantContext.Set(tenantId, userId, companyId, roles);
                }
            }

            await next(context);
        }
        finally
        {
            tenantContext.Clear();
        }
    }
}

