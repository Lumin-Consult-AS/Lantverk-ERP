using System.Security.Claims;

namespace Erp.Core.Domain.Tenancy;

public sealed record ValidatedInternalToken(
    Guid TenantId,
    Guid UserId,
    Guid? CompanyId,
    IReadOnlyList<string> Roles,
    ClaimsPrincipal Principal);

public interface IInternalTokenIssuer
{
    string IssueShortLivedCallToken(
        Guid tenantId,
        Guid userId,
        Guid? companyId = null,
        IReadOnlyList<string>? roles = null,
        TimeSpan? lifetime = null);

    ValidatedInternalToken? ValidateToken(string accessToken);
}

