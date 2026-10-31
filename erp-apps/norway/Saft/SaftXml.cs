using System.Globalization;
using System.Reflection;
using System.Text;
using System.Xml;
using System.Xml.Schema;

namespace Norway.Saft;

/// <summary>
/// Writes a SAF-T Financial file (Skatteetaten's schema version 1.40) and checks it against the official schema, which is
/// part of the app. The element order is the schema's.
/// </summary>
public static class SaftXml
{
    public const string Namespace = "urn:StandardAuditFile-Taxation-Financial:NO";
    public const string SchemaVersion = "1.40";
    public const string SoftwareCompany = "Laftverk";
    public const string SoftwareId = "Laftverk ERP";
    public const string SoftwareVersion = "1.0";

    public static byte[] Write(SaftData data)
    {
        using var stream = new MemoryStream();
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true, IndentChars = "  " };
        using (var w = XmlWriter.Create(stream, settings))
        {
            w.WriteStartDocument();
            w.WriteStartElement("AuditFile", Namespace);
            WriteHeader(w, data);
            WriteMasterFiles(w, data);
            WriteEntries(w, data);
            w.WriteEndElement();
            w.WriteEndDocument();
        }

        return stream.ToArray();
    }

    /// <summary>Returns the schema errors of <paramref name="xml"/> (empty = valid).</summary>
    public static IReadOnlyList<string> Validate(byte[] xml)
    {
        var errors = new List<string>();
        var schemas = new XmlSchemaSet();
        using (var xsd = Assembly.GetExecutingAssembly().GetManifestResourceStream("Norway.Schemas.Norwegian_SAF-T_Financial_Schema_v_1.40.xsd")
                         ?? throw new InvalidOperationException("The SAF-T schema is missing from the app."))
        using (var reader = XmlReader.Create(xsd))
        {
            schemas.Add(Namespace, reader);
        }

        var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema, Schemas = schemas };
        settings.ValidationEventHandler += (_, e) =>
        {
            if (errors.Count < 20)
            {
                errors.Add($"Linje {e.Exception?.LineNumber}: {e.Message}");
            }
        };
        using var input = XmlReader.Create(new MemoryStream(xml), settings);
        while (input.Read())
        {
        }

        return errors;
    }

    // ------------------------------------------------------------------ header and master files

    private static void WriteHeader(XmlWriter w, SaftData d)
    {
        w.WriteStartElement("Header", Namespace);
        Text(w, "AuditFileVersion", SchemaVersion);
        Text(w, "AuditFileCountry", "NO");
        Text(w, "AuditFileDateCreated", Date(d.CreatedOn));
        Text(w, "SoftwareCompanyName", SoftwareCompany);
        Text(w, "SoftwareID", SoftwareId);
        Text(w, "SoftwareVersion", SoftwareVersion);

        w.WriteStartElement("Company", Namespace);
        Text(w, "RegistrationNumber", d.Company.RegistrationNumber);
        Text(w, "Name", d.Company.Name);
        if (d.Company.Street is not null || d.Company.City is not null || d.Company.PostalCode is not null)
        {
            w.WriteStartElement("Address", Namespace);
            Optional(w, "StreetName", d.Company.Street);
            Optional(w, "City", d.Company.City);
            Optional(w, "PostalCode", d.Company.PostalCode);
            Text(w, "Country", "NO");
            w.WriteEndElement();
        }

        w.WriteStartElement("Contact", Namespace);
        w.WriteStartElement("ContactPerson", Namespace);
        Text(w, "FirstName", d.Company.ContactFirstName);
        Text(w, "LastName", d.Company.ContactLastName);
        w.WriteEndElement();
        Optional(w, "Telephone", d.Company.Telephone);
        Optional(w, "Email", d.Company.Email);
        w.WriteEndElement();

        if (d.Company.VatNumber is not null)
        {
            w.WriteStartElement("TaxRegistration", Namespace);
            Text(w, "TaxRegistrationNumber", d.Company.VatNumber);
            w.WriteEndElement();
        }

        w.WriteEndElement();

        Text(w, "DefaultCurrencyCode", d.DefaultCurrency);
        w.WriteStartElement("SelectionCriteria", Namespace);
        Text(w, "PeriodStart", d.From.Month.ToString("00", CultureInfo.InvariantCulture));
        Text(w, "PeriodStartYear", d.From.Year.ToString(CultureInfo.InvariantCulture));
        Text(w, "PeriodEnd", d.To.Month.ToString("00", CultureInfo.InvariantCulture));
        Text(w, "PeriodEndYear", d.To.Year.ToString(CultureInfo.InvariantCulture));
        w.WriteEndElement();
        Text(w, "TaxAccountingBasis", "A");
        w.WriteEndElement();
    }

    private static void WriteMasterFiles(XmlWriter w, SaftData d)
    {
        w.WriteStartElement("MasterFiles", Namespace);

        w.WriteStartElement("GeneralLedgerAccounts", Namespace);
        foreach (var a in d.Accounts)
        {
            w.WriteStartElement("Account", Namespace);
            Text(w, "AccountID", a.Id);
            Text(w, "AccountDescription", a.Description);
            Text(w, "GroupingCategory", a.GroupingCategory);
            Text(w, "GroupingCode", a.GroupingCode);
            Text(w, "AccountType", "GL");
            Balances(w, a.OpeningNet, a.ClosingNet);
            w.WriteEndElement();
        }

        w.WriteEndElement();

        WriteParties(w, "Customers", "Customer", "CustomerID", d.Customers);
        WriteParties(w, "Suppliers", "Supplier", "SupplierID", d.Suppliers);

        w.WriteStartElement("TaxTable", Namespace);
        w.WriteStartElement("TaxTableEntry", Namespace);
        Text(w, "TaxType", "MVA");
        Text(w, "Description", "Merverdiavgift");
        foreach (var t in d.TaxCodes)
        {
            w.WriteStartElement("TaxCodeDetails", Namespace);
            Text(w, "TaxCode", t.Code);
            if (t.EffectiveDate is { } from)
            {
                Text(w, "EffectiveDate", Date(from));
            }

            if (t.ExpirationDate is { } to)
            {
                Text(w, "ExpirationDate", Date(to));
            }

            Text(w, "Description", t.Description);
            Text(w, "TaxPercentage", Number(t.Percentage));
            Text(w, "Country", "NO");
            Text(w, "StandardTaxCode", t.Code);
            Text(w, "BaseRate", "100");
            w.WriteEndElement();
        }

        w.WriteEndElement();
        w.WriteEndElement();

        w.WriteEndElement();
    }

    private static void WriteParties(XmlWriter w, string group, string element, string idElement, IReadOnlyList<SaftParty> parties)
    {
        if (parties.Count == 0)
        {
            return;
        }

        w.WriteStartElement(group, Namespace);
        foreach (var p in parties)
        {
            w.WriteStartElement(element, Namespace);
            Optional(w, "RegistrationNumber", p.RegistrationNumber);
            Text(w, "Name", p.Name);
            Text(w, idElement, p.Id);
            w.WriteStartElement("BalanceAccount", Namespace);
            Text(w, "AccountID", p.AccountId);
            Balances(w, p.OpeningNet, p.ClosingNet);
            w.WriteEndElement();
            w.WriteEndElement();
        }

        w.WriteEndElement();
    }

    private static void Balances(XmlWriter w, decimal opening, decimal closing)
    {
        Text(w, opening >= 0 ? "OpeningDebitBalance" : "OpeningCreditBalance", Money(Math.Abs(opening)));
        Text(w, closing >= 0 ? "ClosingDebitBalance" : "ClosingCreditBalance", Money(Math.Abs(closing)));
    }

    // ------------------------------------------------------------------ entries

    private static void WriteEntries(XmlWriter w, SaftData d)
    {
        w.WriteStartElement("GeneralLedgerEntries", Namespace);
        Text(w, "NumberOfEntries", d.Transactions.Count.ToString(CultureInfo.InvariantCulture));
        Text(w, "TotalDebit", Money(d.Transactions.Sum(t => t.Lines.Sum(l => l.Debit))));
        Text(w, "TotalCredit", Money(d.Transactions.Sum(t => t.Lines.Sum(l => l.Credit))));

        w.WriteStartElement("Journal", Namespace);
        Text(w, "JournalID", "1");
        Text(w, "Description", "Hovedbok");
        Text(w, "Type", "GL");
        foreach (var t in d.Transactions)
        {
            w.WriteStartElement("Transaction", Namespace);
            Text(w, "TransactionID", t.Id);
            Text(w, "Period", t.Period.ToString("00", CultureInfo.InvariantCulture));
            Text(w, "PeriodYear", t.Year.ToString(CultureInfo.InvariantCulture));
            Text(w, "TransactionDate", Date(t.Date));
            Text(w, "Description", t.Description);
            Text(w, "SystemEntryDate", Date(t.EntryDate));
            Text(w, "GLPostingDate", Date(t.Date));
            foreach (var l in t.Lines)
            {
                WriteLine(w, l);
            }

            w.WriteEndElement();
        }

        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void WriteLine(XmlWriter w, SaftLine l)
    {
        w.WriteStartElement("Line", Namespace);
        Text(w, "RecordID", l.RecordId);
        Text(w, "AccountID", l.AccountId);
        Optional(w, "CustomerID", l.CustomerId);
        Optional(w, "SupplierID", l.SupplierId);
        Text(w, "Description", l.Description);

        var element = l.Debit > 0 ? "DebitAmount" : "CreditAmount";
        w.WriteStartElement(element, Namespace);
        Text(w, "Amount", Money(l.Debit > 0 ? l.Debit : l.Credit));
        if (l.Currency is not null && l.CurrencyAmount is { } foreign)
        {
            Text(w, "CurrencyCode", l.Currency);
            Text(w, "CurrencyAmount", Number(foreign));
            if (l.ExchangeRate is { } rate)
            {
                Text(w, "ExchangeRate", Number(rate));
            }
        }

        w.WriteEndElement();

        if (l.Tax is { } tax)
        {
            w.WriteStartElement("TaxInformation", Namespace);
            Text(w, "TaxType", "MVA");
            Text(w, "TaxCode", tax.Code);
            Text(w, "TaxPercentage", Number(tax.Percentage));
            Text(w, "TaxBase", Number(tax.Base));
            w.WriteStartElement(l.Debit > 0 ? "DebitTaxAmount" : "CreditTaxAmount", Namespace);
            Text(w, "Amount", Money(tax.Amount));
            w.WriteEndElement();
            w.WriteEndElement();
        }

        if (l.DueDate is { } due)
        {
            Text(w, "DueDate", Date(due));
        }

        w.WriteEndElement();
    }

    // ------------------------------------------------------------------ formatting

    private static void Text(XmlWriter w, string name, string value) => w.WriteElementString(name, Namespace, value);

    private static void Optional(XmlWriter w, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            Text(w, name, value);
        }
    }

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);

    private static string Number(decimal value) => value.ToString("0.##########", CultureInfo.InvariantCulture);
}
