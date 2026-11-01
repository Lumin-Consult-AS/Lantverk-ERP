namespace Erp.Core.Domain.Entities;

/// <summary>
/// Core object: core.user. User belonging to a single tenant, mapped via OIDC subject.
/// </summary>
public sealed class User : EntityBase
{
    public string ExternalSubject { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = [];
    public List<Guid> AccessibleCompanyIds { get; set; } = [];
    public bool IsActive { get; set; } = true;

    /// <summary>The user's language (a code such as nb or en), or null to use the tenant's default.</summary>
    public string? Language { get; set; }

    /// <summary>The user's region (a country code such as NO), or null to use the tenant's default.</summary>
    public string? Region { get; set; }
}

