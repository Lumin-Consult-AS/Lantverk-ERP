namespace Norway.Saft;

/// <summary>What goes into a SAF-T Financial file, collected from the ledger and independent of how it is written.</summary>
public sealed record SaftCompany(
    string Name,
    string RegistrationNumber,
    string? VatNumber,
    string? Street,
    string? PostalCode,
    string? City,
    string ContactFirstName,
    string ContactLastName,
    string? Telephone,
    string? Email);

/// <summary>An account with balances as net amounts (debit positive, credit negative).</summary>
public sealed record SaftAccount(string Id, string Description, string GroupingCategory, string GroupingCode, decimal OpeningNet, decimal ClosingNet);

public sealed record SaftParty(string Id, string Name, string? RegistrationNumber, string AccountId, decimal OpeningNet, decimal ClosingNet);

public sealed record SaftTaxCode(string Code, string Description, decimal Percentage, DateOnly? EffectiveDate, DateOnly? ExpirationDate);

/// <summary>VAT on a base line: the code, its rate, the basis and the VAT amount (on the same side as the base line).</summary>
public sealed record SaftTax(string Code, decimal Percentage, decimal Base, decimal Amount);

public sealed record SaftLine(
    string RecordId,
    string AccountId,
    string Description,
    decimal Debit,
    decimal Credit,
    string? CustomerId,
    string? SupplierId,
    DateOnly? DueDate,
    string? Currency,
    decimal? CurrencyAmount,
    decimal? ExchangeRate,
    SaftTax? Tax);

public sealed record SaftTransaction(
    string Id, int Period, int Year, DateOnly Date, string Description, DateOnly EntryDate, IReadOnlyList<SaftLine> Lines);

public sealed record SaftData(
    SaftCompany Company,
    string DefaultCurrency,
    DateOnly CreatedOn,
    DateOnly From,
    DateOnly To,
    IReadOnlyList<SaftAccount> Accounts,
    IReadOnlyList<SaftParty> Customers,
    IReadOnlyList<SaftParty> Suppliers,
    IReadOnlyList<SaftTaxCode> TaxCodes,
    IReadOnlyList<SaftTransaction> Transactions);
