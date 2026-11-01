namespace Erp.Core.Domain.Entities;

/// <summary>
/// Core object: core.tenant. Represents an isolated customer tenant.
/// </summary>
public sealed class Tenant : EntityBase
{
    public string Name { get; set; } = string.Empty;
    public string Slug { get; set; } = string.Empty;
    public string DefaultCountryCode { get; set; } = "NO";

    /// <summary>The language a user gets who has not chosen one (the default region is <see cref="DefaultCountryCode"/>).</summary>
    public string DefaultLanguage { get; set; } = "nb";
    public bool IsActive { get; set; } = true;
}

