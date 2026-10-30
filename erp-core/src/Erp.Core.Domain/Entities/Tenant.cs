namespace Erp.Core.Domain.Entities;

/// <summary>
/// Core object: core.tenant. Represents an isolated customer tenant.
/// </summary>
public sealed class Tenant : EntityBase
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string DefaultCountryCode { get; set; } = "NO";
    public bool IsActive { get; set; } = true;
}

