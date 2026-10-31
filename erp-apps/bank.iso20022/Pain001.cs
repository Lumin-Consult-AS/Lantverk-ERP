using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Schema;

namespace Bank.Iso20022;

public sealed record PaymentInstruction(
    string Id, string CreditorName, string CreditorAccount, decimal Amount, string Currency, string? Reference, string? Message);

public sealed record PaymentFile(
    string MessageId,
    DateTime CreatedAt,
    DateOnly ExecutionDate,
    string DebtorName,
    string? DebtorId,
    string DebtorAccount,
    string? DebtorBic,
    string Currency,
    IReadOnlyList<PaymentInstruction> Payments);

/// <summary>
/// Writes a customer credit transfer initiation, pain.001.001.03 (the version Norwegian banks accept), and checks it against the
/// ISO 20022 schema, which is part of the app. A structured reference (KID) is sent as a creditor reference; otherwise the
/// message goes as unstructured remittance information.
/// </summary>
public static partial class Pain001
{
    public const string Namespace = "urn:iso:std:iso:20022:tech:xsd:pain.001.001.03";

    public static byte[] Write(PaymentFile file)
    {
        var count = file.Payments.Count.ToString(CultureInfo.InvariantCulture);
        var sum = Money(file.Payments.Sum(p => p.Amount));

        using var stream = new MemoryStream();
        using (var w = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true }))
        {
            w.WriteStartDocument();
            w.WriteStartElement("Document", Namespace);
            w.WriteStartElement("CstmrCdtTrfInitn", Namespace);

            w.WriteStartElement("GrpHdr", Namespace);
            Text(w, "MsgId", file.MessageId);
            Text(w, "CreDtTm", file.CreatedAt.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
            Text(w, "NbOfTxs", count);
            Text(w, "CtrlSum", sum);
            w.WriteStartElement("InitgPty", Namespace);
            Text(w, "Nm", Limit(file.DebtorName, 70));
            if (file.DebtorId is not null)
            {
                OrganisationId(w, file.DebtorId);
            }

            w.WriteEndElement();
            w.WriteEndElement();

            w.WriteStartElement("PmtInf", Namespace);
            Text(w, "PmtInfId", file.MessageId);
            Text(w, "PmtMtd", "TRF");
            Text(w, "BtchBookg", "true");
            Text(w, "NbOfTxs", count);
            Text(w, "CtrlSum", sum);
            w.WriteStartElement("PmtTpInf", Namespace);
            w.WriteStartElement("SvcLvl", Namespace);
            Text(w, "Cd", "NURG");
            w.WriteEndElement();
            w.WriteEndElement();
            Text(w, "ReqdExctnDt", file.ExecutionDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            w.WriteStartElement("Dbtr", Namespace);
            Text(w, "Nm", Limit(file.DebtorName, 70));
            if (file.DebtorId is not null)
            {
                OrganisationId(w, file.DebtorId);
            }

            w.WriteEndElement();
            w.WriteStartElement("DbtrAcct", Namespace);
            AccountId(w, file.DebtorAccount);
            Text(w, "Ccy", file.Currency);
            w.WriteEndElement();
            w.WriteStartElement("DbtrAgt", Namespace);
            w.WriteStartElement("FinInstnId", Namespace);
            if (file.DebtorBic is not null)
            {
                Text(w, "BIC", file.DebtorBic);
            }
            else
            {
                w.WriteStartElement("Othr", Namespace);
                Text(w, "Id", "NOTPROVIDED");
                w.WriteEndElement();
            }

            w.WriteEndElement();
            w.WriteEndElement();
            Text(w, "ChrgBr", "SLEV");

            foreach (var p in file.Payments)
            {
                w.WriteStartElement("CdtTrfTxInf", Namespace);
                w.WriteStartElement("PmtId", Namespace);
                Text(w, "InstrId", p.Id);
                Text(w, "EndToEndId", p.Id);
                w.WriteEndElement();
                w.WriteStartElement("Amt", Namespace);
                w.WriteStartElement("InstdAmt", Namespace);
                w.WriteAttributeString("Ccy", p.Currency);
                w.WriteString(Money(p.Amount));
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteStartElement("Cdtr", Namespace);
                Text(w, "Nm", Limit(p.CreditorName, 70));
                w.WriteEndElement();
                w.WriteStartElement("CdtrAcct", Namespace);
                AccountId(w, p.CreditorAccount);
                w.WriteEndElement();
                if (p.Reference is not null || p.Message is not null)
                {
                    w.WriteStartElement("RmtInf", Namespace);
                    if (p.Reference is not null)
                    {
                        w.WriteStartElement("Strd", Namespace);
                        w.WriteStartElement("CdtrRefInf", Namespace);
                        w.WriteStartElement("Tp", Namespace);
                        w.WriteStartElement("CdOrPrtry", Namespace);
                        Text(w, "Prtry", "KID");
                        w.WriteEndElement();
                        w.WriteEndElement();
                        Text(w, "Ref", Limit(p.Reference, 35));
                        w.WriteEndElement();
                        w.WriteEndElement();
                    }
                    else
                    {
                        Text(w, "Ustrd", Limit(p.Message!, 140));
                    }

                    w.WriteEndElement();
                }

                w.WriteEndElement();
            }

            w.WriteEndElement();
            w.WriteEndElement();
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
        using (var xsd = Assembly.GetExecutingAssembly().GetManifestResourceStream("Bank.Iso20022.Schemas.pain.001.001.03.xsd")
                         ?? throw new InvalidOperationException("The pain.001 schema is missing from the app."))
        using (var reader = XmlReader.Create(xsd))
        {
            schemas.Add(Namespace, reader);
        }

        var settings = new XmlReaderSettings { ValidationType = ValidationType.Schema, Schemas = schemas };
        settings.ValidationEventHandler += (_, e) =>
        {
            if (errors.Count < 10)
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

    /// <summary>Removes spaces and dots from an account number; an IBAN has two letters first.</summary>
    public static string NormalizeAccount(string account) => account.Replace(" ", string.Empty).Replace(".", string.Empty).Trim().ToUpperInvariant();

    private static void AccountId(XmlWriter w, string account)
    {
        var normalized = NormalizeAccount(account);
        w.WriteStartElement("Id", Namespace);
        if (IbanPattern().IsMatch(normalized))
        {
            Text(w, "IBAN", normalized);
        }
        else
        {
            w.WriteStartElement("Othr", Namespace);
            Text(w, "Id", normalized);
            w.WriteEndElement();
        }

        w.WriteEndElement();
    }

    private static void OrganisationId(XmlWriter w, string id)
    {
        w.WriteStartElement("Id", Namespace);
        w.WriteStartElement("OrgId", Namespace);
        w.WriteStartElement("Othr", Namespace);
        Text(w, "Id", Limit(id, 35));
        w.WriteEndElement();
        w.WriteEndElement();
        w.WriteEndElement();
    }

    private static void Text(XmlWriter w, string name, string value) => w.WriteElementString(name, Namespace, value);

    private static string Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero).ToString("0.00", CultureInfo.InvariantCulture);

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];

    [GeneratedRegex("^[A-Z]{2}[0-9]{2}[A-Z0-9]{1,30}$")]
    private static partial Regex IbanPattern();
}
