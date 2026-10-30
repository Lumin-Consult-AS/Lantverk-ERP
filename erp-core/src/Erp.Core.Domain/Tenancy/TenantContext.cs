namespace Erp.Core.Domain.Tenancy;

/// <summary>
/// Scoped implementation of <see cref="ITenantContext"/>.
/// </summary>
public sealed class TenantContext : ITenantContext
{
    public Guid? TenantId { get; private set; }
    public Guid? CompanyId { get; private set; }
    public Guid? UserId { get; private set; }
    public IReadOnlyList<string> Roles { get; private set; } = [];
    public bool IsAuthenticated => TenantId.HasValue && UserId.HasValue;

    public void Set(Guid tenantId, Guid? userId = null, Guid? companyId = null, IReadOnlyList<string>? roles = null)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("TenantId cannot be empty.", nameof(tenantId));
        }

        TenantId = tenantId;
        UserId = userId;
        CompanyId = companyId;
        Roles = roles ?? [];
    }

    public void Clear()
    {
        TenantId = null;
        UserId = null;
        CompanyId = null;
        Roles = [];
    }

    public IDisposable BeginScope(Guid tenantId, Guid? userId = null, Guid? companyId = null, IReadOnlyList<string>? roles = null)
    {
        var previousTenantId = TenantId;
        var previousUserId = UserId;
        var previousCompanyId = CompanyId;
        var previousRoles = Roles;

        Set(tenantId, userId, companyId, roles);

        return new ScopeResetter(() =>
        {
            TenantId = previousTenantId;
            UserId = previousUserId;
            CompanyId = previousCompanyId;
            Roles = previousRoles;
        });
    }

    private sealed class ScopeResetter(Action onDispose) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                onDispose();
            }
        }
    }
}

