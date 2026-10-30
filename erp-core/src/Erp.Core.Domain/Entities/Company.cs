namespace Erp.Core.Domain.Entities;

/// <summary>
/// Core object: core.company. Legal entity belonging to a tenant.
/// </summary>
public sealed class Company : EntityBase
{
    public string Name { get; set; } = string.Empty;
    public string OrganizationNumber { get; set; } = string.Empty;
    public Guid CountryId { get; set; }
    public Guid BaseCurrencyId { get; set; }
    public bool IsActive { get; set; } = true;
}

