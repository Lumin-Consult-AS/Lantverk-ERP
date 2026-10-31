namespace Erp.Core.Domain.Apps;

/// <summary>
/// A retention rule an app brings (ADR-0026): files attached in <see cref="Category"/> for a company in
/// <see cref="Country"/> are kept <see cref="Years"/> years. The core applies it without knowing why; a country's
/// legislation therefore lives in the app that implements the country.
/// </summary>
public sealed class RetentionRuleDefinition
{
    public string Country { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public int Years { get; set; }
}
