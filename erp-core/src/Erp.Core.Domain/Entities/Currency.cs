namespace Erp.Core.Domain.Entities;

/// <summary>
/// Core object: core.currency. Currency definition within a tenant.
/// </summary>
public sealed class Currency : EntityBase
{
    public string IsoCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Symbol { get; set; } = string.Empty;
    public int Decimals { get; set; } = 2;
}

