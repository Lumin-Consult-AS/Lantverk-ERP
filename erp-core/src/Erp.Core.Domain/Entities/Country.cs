namespace Erp.Core.Domain.Entities;

/// <summary>
/// Core object: core.country. First-class country dimension in the core.
/// </summary>
public sealed class Country : EntityBase
{
    public string IsoCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string DefaultCurrencyCode { get; set; } = string.Empty;
}

