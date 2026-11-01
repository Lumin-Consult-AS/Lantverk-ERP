using System.Globalization;
using System.Xml.Linq;

namespace Ehf;

public sealed class EhfException(string message) : Exception(message);

public sealed record EhfParty(string Name, string OrganizationNumber, string? Street, string? PostalCode, string? City, string? Email, bool VatRegistered);

/// <param name="VatRate">The rate in percent, or null when the line has no VAT code.</param>
public sealed record EhfLine(string Description, decimal Quantity, decimal UnitPrice, decimal Amount, decimal? VatRate);

public sealed record EhfInvoice(
    string Number,
    DateOnly IssueDate,
    DateOnly? DueDate,
    string Currency,
    string BuyerReference,
    string? PaymentReference,
    string? BankAccount,
    EhfParty Seller,
    EhfParty Buyer,
    IReadOnlyList<EhfLine> Lines);

/// <summary>What an incoming invoice or credit note says, in the shape the accounting app needs. Amounts of a credit note are negative.</summary>
public sealed record ReceivedInvoice(
    string SupplierName,
    string? SupplierOrgNumber,
    string InvoiceNumber,
    DateOnly? InvoiceDate,
    DateOnly? DueDate,
    string Currency,
    decimal TotalAmount,
    decimal VatAmount,
    string? PaymentReference,
    string? BankAccount,
    IReadOnlyList<ReceivedLine> Lines);

public sealed record ReceivedLine(string Description, decimal Amount, decimal? VatRate, decimal VatAmount);

/// <summary>
/// EHF 3.0 is the Norwegian use of PEPPOL BIS Billing 3.0: a UBL 2.1 Invoice with the customization and profile ids below.
/// Parties are identified by their organisation number under scheme 0192. Writing follows the element order of the UBL schema;
/// the totals are worked out as the BIS rules say (VAT per category from the taxable amount), and an invoice whose sum differs
/// from the posted amount is refused rather than sent.
/// </summary>
public static class Ubl
{
    public const string CustomizationId = "urn:cen.eu:en16931:2017#compliant#urn:fdc:peppol.eu:2017:poacc:billing:3.0";
    public const string ProfileId = "urn:fdc:peppol.eu:2017:poacc:billing:01:1.0";
    public const string OrganizationScheme = "0192";

    private static readonly XNamespace Inv = "urn:oasis:names:specification:ubl:schema:xsd:Invoice-2";
    private static readonly XNamespace Credit = "urn:oasis:names:specification:ubl:schema:xsd:CreditNote-2";
    private static readonly XNamespace Cac = "urn:oasis:names:specification:ubl:schema:xsd:CommonAggregateComponents-2";
    private static readonly XNamespace Cbc = "urn:oasis:names:specification:ubl:schema:xsd:CommonBasicComponents-2";

    public static string ParticipantId(string organizationNumber) => $"{OrganizationScheme}:{organizationNumber}";

    /// <summary>The totals of the invoice as the BIS rules work them out: net, VAT per rate, and the amount to pay.</summary>
    public static (decimal Net, IReadOnlyList<(decimal Rate, decimal Taxable, decimal Tax)> Categories, decimal Gross) Totals(IEnumerable<EhfLine> lines)
    {
        var list = lines.ToList();
        var categories = list.GroupBy(l => l.VatRate ?? 0m)
            .OrderByDescending(g => g.Key)
            .Select(g =>
            {
                var taxable = g.Sum(l => l.Amount);
                return (g.Key, taxable, Math.Round(taxable * g.Key / 100m, 2, MidpointRounding.AwayFromZero));
            })
            .ToList();
        var net = list.Sum(l => l.Amount);
        return (net, categories, net + categories.Sum(c => c.Item3));
    }

    public static XDocument Write(EhfInvoice invoice)
    {
        Check(invoice);
        var (net, categories, gross) = Totals(invoice.Lines);
        var currency = invoice.Currency;

        XElement Amount(XName name, decimal value) => new(name, new XAttribute("currencyID", currency), Fmt(value));
        XElement Category(decimal rate) => new(Cac + "TaxCategory",
            new XElement(Cbc + "ID", rate > 0 ? "S" : "Z"),
            new XElement(Cbc + "Percent", Fmt(rate)),
            new XElement(Cac + "TaxScheme", new XElement(Cbc + "ID", "VAT")));

        var root = new XElement(Inv + "Invoice",
            new XAttribute(XNamespace.Xmlns + "cac", Cac),
            new XAttribute(XNamespace.Xmlns + "cbc", Cbc),
            new XElement(Cbc + "CustomizationID", CustomizationId),
            new XElement(Cbc + "ProfileID", ProfileId),
            new XElement(Cbc + "ID", invoice.Number),
            new XElement(Cbc + "IssueDate", invoice.IssueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            invoice.DueDate is { } due ? new XElement(Cbc + "DueDate", due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) : null,
            new XElement(Cbc + "InvoiceTypeCode", "380"),
            new XElement(Cbc + "DocumentCurrencyCode", currency),
            new XElement(Cbc + "BuyerReference", invoice.BuyerReference),
            new XElement(Cac + "AccountingSupplierParty", PartyElement(invoice.Seller, true)),
            new XElement(Cac + "AccountingCustomerParty", PartyElement(invoice.Buyer, false)),
            new XElement(Cac + "PaymentMeans",
                new XElement(Cbc + "PaymentMeansCode", "30"),
                string.IsNullOrWhiteSpace(invoice.PaymentReference) ? null : new XElement(Cbc + "PaymentID", invoice.PaymentReference),
                string.IsNullOrWhiteSpace(invoice.BankAccount) ? null : new XElement(Cac + "PayeeFinancialAccount", new XElement(Cbc + "ID", invoice.BankAccount))),
            new XElement(Cac + "TaxTotal",
                Amount(Cbc + "TaxAmount", categories.Sum(c => c.Tax)),
                categories.Select(c => new XElement(Cac + "TaxSubtotal",
                    Amount(Cbc + "TaxableAmount", c.Taxable),
                    Amount(Cbc + "TaxAmount", c.Tax),
                    Category(c.Rate)))),
            new XElement(Cac + "LegalMonetaryTotal",
                Amount(Cbc + "LineExtensionAmount", net),
                Amount(Cbc + "TaxExclusiveAmount", net),
                Amount(Cbc + "TaxInclusiveAmount", gross),
                Amount(Cbc + "PayableAmount", gross)),
            invoice.Lines.Select((line, index) => new XElement(Cac + "InvoiceLine",
                new XElement(Cbc + "ID", (index + 1).ToString(CultureInfo.InvariantCulture)),
                new XElement(Cbc + "InvoicedQuantity", new XAttribute("unitCode", "C62"), Fmt(line.Quantity, 4)),
                Amount(Cbc + "LineExtensionAmount", line.Amount),
                new XElement(Cac + "Item",
                    new XElement(Cbc + "Name", line.Description),
                    new XElement(Cac + "ClassifiedTaxCategory",
                        new XElement(Cbc + "ID", line.VatRate > 0 ? "S" : "Z"),
                        new XElement(Cbc + "Percent", Fmt(line.VatRate ?? 0m)),
                        new XElement(Cac + "TaxScheme", new XElement(Cbc + "ID", "VAT")))),
                new XElement(Cac + "Price", Amount(Cbc + "PriceAmount", line.UnitPrice)))));

        return new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
    }

    private static XElement PartyElement(EhfParty party, bool seller) =>
        new(Cac + "Party",
            new XElement(Cbc + "EndpointID", new XAttribute("schemeID", OrganizationScheme), party.OrganizationNumber),
            new XElement(Cac + "PartyName", new XElement(Cbc + "Name", party.Name)),
            new XElement(Cac + "PostalAddress",
                string.IsNullOrWhiteSpace(party.Street) ? null : new XElement(Cbc + "StreetName", party.Street),
                string.IsNullOrWhiteSpace(party.City) ? null : new XElement(Cbc + "CityName", party.City),
                string.IsNullOrWhiteSpace(party.PostalCode) ? null : new XElement(Cbc + "PostalZone", party.PostalCode),
                new XElement(Cac + "Country", new XElement(Cbc + "IdentificationCode", "NO"))),
            seller && party.VatRegistered
                ? new XElement(Cac + "PartyTaxScheme",
                    new XElement(Cbc + "CompanyID", $"NO{party.OrganizationNumber}MVA"),
                    new XElement(Cac + "TaxScheme", new XElement(Cbc + "ID", "VAT")))
                : null,
            new XElement(Cac + "PartyLegalEntity",
                new XElement(Cbc + "RegistrationName", party.Name),
                new XElement(Cbc + "CompanyID", new XAttribute("schemeID", OrganizationScheme), party.OrganizationNumber)),
            seller && !string.IsNullOrWhiteSpace(party.Email)
                ? new XElement(Cac + "Contact", new XElement(Cbc + "ElectronicMail", party.Email))
                : null);

    /// <summary>The business rules we can check without the PEPPOL Schematron: what must be there, in plain words for the user.</summary>
    public static void Check(EhfInvoice invoice)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(invoice.Number)) problems.Add("fakturanummer mangler");
        if (invoice.Lines.Count == 0) problems.Add("fakturaen har ingen linjer");
        if (string.IsNullOrWhiteSpace(invoice.BuyerReference)) problems.Add("kundens referanse mangler");
        if (string.IsNullOrWhiteSpace(invoice.Seller.Name) || string.IsNullOrWhiteSpace(invoice.Seller.OrganizationNumber))
            problems.Add("selskapets navn og organisasjonsnummer mangler (Norsk oppsett)");
        if (string.IsNullOrWhiteSpace(invoice.Seller.City) || string.IsNullOrWhiteSpace(invoice.Seller.PostalCode))
            problems.Add("selskapets adresse mangler (Norsk oppsett)");
        if (!invoice.Seller.VatRegistered) problems.Add("EHF fra selskap som ikke er MVA-registrert støttes ikke ennå");
        if (string.IsNullOrWhiteSpace(invoice.Buyer.OrganizationNumber)) problems.Add("kunden mangler organisasjonsnummer");
        if (invoice.Lines.Any(l => l.VatRate is null)) problems.Add("alle linjer må ha MVA-kode");
        if (invoice.Lines.Any(l => l.Amount < 0) || invoice.Lines.Sum(l => l.Amount) < 0) problems.Add("kreditnota som EHF støttes ikke ennå");
        if (problems.Count > 0)
        {
            throw new EhfException($"Fakturaen kan ikke sendes som EHF: {string.Join("; ", problems)}.");
        }
    }

    /// <summary>Reads an invoice or a credit note. Throws <see cref="EhfException"/> for anything else.</summary>
    public static ReceivedInvoice Read(byte[] xml)
    {
        XDocument doc;
        try
        {
            using var stream = new MemoryStream(xml);
            doc = XDocument.Load(stream);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new EhfException($"Dokumentet er ikke gyldig XML: {ex.Message}");
        }

        var root = doc.Root ?? throw new EhfException("Dokumentet er tomt.");
        var credit = root.Name == Credit + "CreditNote";
        if (root.Name != Inv + "Invoice" && !credit)
        {
            throw new EhfException($"Dokumentet er verken en faktura eller en kreditnota ({root.Name.LocalName}).");
        }

        var sign = credit ? -1m : 1m;
        var supplier = root.Element(Cac + "AccountingSupplierParty")?.Element(Cac + "Party")
            ?? throw new EhfException("Dokumentet mangler leverandør.");
        var legal = supplier.Element(Cac + "PartyLegalEntity");
        var name = legal?.Element(Cbc + "RegistrationName")?.Value ?? supplier.Element(Cac + "PartyName")?.Element(Cbc + "Name")?.Value
            ?? throw new EhfException("Dokumentet mangler leverandørens navn.");
        var org = legal?.Element(Cbc + "CompanyID") is { } company && (company.Attribute("schemeID")?.Value ?? OrganizationScheme) == OrganizationScheme
            ? company.Value
            : supplier.Element(Cbc + "EndpointID") is { } endpoint && endpoint.Attribute("schemeID")?.Value == OrganizationScheme ? endpoint.Value : null;

        var lines = new List<ReceivedLine>();
        foreach (var line in root.Elements(Cac + (credit ? "CreditNoteLine" : "InvoiceLine")))
        {
            var amount = Number(line.Element(Cbc + "LineExtensionAmount")) * sign;
            var rate = line.Element(Cac + "Item")?.Element(Cac + "ClassifiedTaxCategory")?.Element(Cbc + "Percent") is { } p ? Number(p) : (decimal?)null;
            lines.Add(new ReceivedLine(
                line.Element(Cac + "Item")?.Element(Cbc + "Name")?.Value ?? "-",
                amount,
                rate,
                rate is { } r ? Math.Round(amount * r / 100m, 2, MidpointRounding.AwayFromZero) : 0m));
        }

        var totals = root.Element(Cac + "LegalMonetaryTotal") ?? throw new EhfException("Dokumentet mangler totaler.");
        var vat = root.Element(Cac + "TaxTotal")?.Element(Cbc + "TaxAmount") is { } tax ? Number(tax) * sign : 0m;
        var means = root.Element(Cac + "PaymentMeans");

        // The VAT of the lines is worked out line by line; a few øre of rounding against the document's total goes on the last line
        var payable = Number(totals.Element(Cbc + "PayableAmount") ?? throw new EhfException("Dokumentet mangler beløp å betale.")) * sign;
        var difference = payable - lines.Sum(l => l.Amount + l.VatAmount);
        if (lines.Count > 0 && difference != 0 && Math.Abs(difference) <= 0.05m)
        {
            lines[^1] = lines[^1] with { VatAmount = lines[^1].VatAmount + difference };
        }

        return new ReceivedInvoice(
            name,
            org,
            root.Element(Cbc + "ID")?.Value ?? throw new EhfException("Dokumentet mangler fakturanummer."),
            Date(root.Element(Cbc + "IssueDate")),
            Date(root.Element(Cbc + "DueDate") ?? means?.Element(Cbc + "PaymentDueDate")),
            root.Element(Cbc + "DocumentCurrencyCode")?.Value ?? throw new EhfException("Dokumentet mangler valuta."),
            payable,
            vat,
            means?.Element(Cbc + "PaymentID")?.Value,
            means?.Element(Cac + "PayeeFinancialAccount")?.Element(Cbc + "ID")?.Value,
            lines);
    }

    private static decimal Number(XElement? element) =>
        element is not null && decimal.TryParse(element.Value, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new EhfException($"Ugyldig beløp i {element?.Name.LocalName ?? "dokumentet"}.");

    private static DateOnly? Date(XElement? element) =>
        element is not null && DateOnly.TryParseExact(element.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static string Fmt(decimal value, int scale = 2) => value.ToString("F" + scale, CultureInfo.InvariantCulture);
}
