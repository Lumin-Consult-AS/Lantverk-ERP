namespace Erp.Core.Domain.Tenancy;

/// <summary>
/// Provides the active tenant, company, user, and role context for the current operation.
/// </summary>
public interface ITenantContext
{
    Guid? TenantId { get; }
    Guid? CompanyId { get; }
    Guid? UserId { get; }
    IReadOnlyList<string> Roles { get; }
    bool IsAuthenticated { get; }

    void Set(Guid tenantId, Guid? userId = null, Guid? companyId = null, IReadOnlyList<string>? roles = null);
    void Clear();
    IDisposable BeginScope(Guid tenantId, Guid? userId = null, Guid? companyId = null, IReadOnlyList<string>? roles = null);
}

