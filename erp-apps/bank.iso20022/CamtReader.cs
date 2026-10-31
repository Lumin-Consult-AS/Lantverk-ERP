using System.Globalization;
using System.Xml.Linq;

namespace Bank.Iso20022;

public sealed record BankEntry(
    string Reference,
    DateOnly BookingDate,
    DateOnly? ValueDate,
    decimal Amount,
    string Currency,
    string? CounterpartyName,
    string? CounterpartyAccount,
    string? PaymentReference,
    string? Message,
    string? EndToEndId);

public sealed record BankStatementData(
    string? StatementId,
    string AccountId,
    DateOnly? From,
    DateOnly? To,
    decimal? OpeningBalance,
    decimal? ClosingBalance,
    string? Currency,
    IReadOnlyList<BankEntry> Entries);

public sealed class BankFileException(string message) : Exception(message);

/// <summary>
/// Reads camt.053 (statement) and camt.054 (debit/credit notification) files. The reader follows the structure and ignores the
/// message version, so the versions banks actually send (…001.02 and later) are read the same way. An entry becomes one bank
/// transaction (credit positive, debit negative); an entry with several details is kept as one with the first detail.
/// </summary>
public static class CamtReader
{
    /// <summary>Whether the file is a statement (camt.053) rather than a notification (camt.054).</summary>
    public static IReadOnlyList<BankStatementData> Read(byte[] xml, out bool isStatement)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Load(new MemoryStream(xml));
        }
        catch (System.Xml.XmlException ex)
        {
            throw new BankFileException($"Filen er ikke gyldig XML: {ex.Message}");
        }

        var root = doc.Root?.Elements().FirstOrDefault()
            ?? throw new BankFileException("Filen er tom.");
        isStatement = root.Name.LocalName == "BkToCstmrStmt";
        if (!isStatement && root.Name.LocalName != "BkToCstmrDbtCdtNtfctn")
        {
            throw new BankFileException("Filen er hverken en kontoutskrift (camt.053) eller en innbetalingsmelding (camt.054).");
        }

        var result = new List<BankStatementData>();
        foreach (var stmt in root.Elements().Where(e => e.Name.LocalName is "Stmt" or "Ntfctn"))
        {
            var account = Account(stmt.Child("Acct")) ?? throw new BankFileException("Utskriften mangler kontonummer.");
            var statementId = stmt.Child("Id")?.Value;
            var balances = stmt.Children("Bal").ToList();
            var entries = new List<BankEntry>();
            var index = 0;
            foreach (var ntry in stmt.Children("Ntry"))
            {
                index++;
                if (ntry.Child("Sts")?.Value is { } status && status is not ("BOOK" or "BOOKED"))
                {
                    continue;
                }

                var amountElement = ntry.Child("Amt") ?? throw new BankFileException("En post mangler beløp.");
                var amount = Amount(amountElement.Value);
                var credit = ntry.Child("CdtDbtInd")?.Value == "CRDT";
                var detail = ntry.Child("NtryDtls")?.Child("TxDtls");
                var refs = detail?.Child("Refs");
                var parties = detail?.Child("RltdPties");
                var booking = Date(ntry.Child("BookgDt")) ?? throw new BankFileException("En post mangler bokføringsdato.");
                var reference = ntry.Child("AcctSvcrRef")?.Value ?? refs?.Child("AcctSvcrRef")?.Value ?? $"{statementId}#{index}";
                var remittance = detail?.Child("RmtInf");

                entries.Add(new BankEntry(
                    reference,
                    booking,
                    Date(ntry.Child("ValDt")),
                    credit ? amount : -amount,
                    amountElement.Attribute("Ccy")?.Value ?? "",
                    credit ? parties?.Child("Dbtr")?.Child("Nm")?.Value : parties?.Child("Cdtr")?.Child("Nm")?.Value,
                    Account(credit ? parties?.Child("DbtrAcct") : parties?.Child("CdtrAcct")),
                    remittance?.Child("Strd")?.Child("CdtrRefInf")?.Child("Ref")?.Value,
                    remittance is null ? ntry.Child("AddtlNtryInf")?.Value : string.Join(" ", remittance.Children("Ustrd").Select(u => u.Value)).NullIfEmpty() ?? ntry.Child("AddtlNtryInf")?.Value,
                    refs?.Child("EndToEndId")?.Value is { } e2e && e2e != "NOTPROVIDED" ? e2e : null));
            }

            decimal? Balance(params string[] codes)
            {
                var bal = balances.FirstOrDefault(b => codes.Contains(b.Child("Tp")?.Child("CdOrPrtry")?.Child("Cd")?.Value));
                return bal is null
                    ? null
                    : Amount(bal.Child("Amt")!.Value) * (bal.Child("CdtDbtInd")?.Value == "DBIT" ? -1 : 1);
            }

            var period = stmt.Child("FrToDt");
            result.Add(new BankStatementData(
                statementId,
                account,
                Date(period?.Child("FrDtTm")),
                Date(period?.Child("ToDtTm")),
                Balance("OPBD", "PRCD"),
                Balance("CLBD", "CLAV"),
                balances.FirstOrDefault()?.Child("Amt")?.Attribute("Ccy")?.Value ?? entries.FirstOrDefault()?.Currency,
                entries));
        }

        return result;
    }

    /// <summary>ISO 20022 amounts use a decimal point and no thousands separator; anything else is refused rather than misread.</summary>
    private static decimal Amount(string text) =>
        decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
            ? value
            : throw new BankFileException($"Ugyldig beløp i filen: «{text}».");

    private static string? Account(XElement? acct)
    {
        var id = acct?.Child("Id");
        var value = id?.Child("IBAN")?.Value ?? id?.Child("Othr")?.Child("Id")?.Value;
        return value is null ? null : Pain001.NormalizeAccount(value);
    }

    private static DateOnly? Date(XElement? holder)
    {
        var text = holder?.Child("Dt")?.Value ?? holder?.Child("DtTm")?.Value ?? holder?.Value;
        return text is { Length: >= 10 } && DateOnly.TryParseExact(text[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private static XElement? Child(this XElement? parent, string name) => parent?.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    private static IEnumerable<XElement> Children(this XElement parent, string name) => parent.Elements().Where(e => e.Name.LocalName == name);

    private static string? NullIfEmpty(this string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
