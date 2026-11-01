using System.Globalization;
using System.Reflection;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;

namespace Norway.Mva;

/// <summary>One line of the VAT return as Skatteetaten wants it: per SAF-T VAT code, whole kroner.</summary>
/// <param name="Basis">Only on output lines; null on input lines.</param>
/// <param name="Rate">Only on output lines, as the rate list writes it ("25", "11,11").</param>
/// <param name="Vat">Positive for output VAT, negative for deductible input VAT.</param>
public sealed record MeldingLine(string Code, string AccountingCode, long? Basis, string? Rate, long Vat);

public sealed record Melding(
    string Reference,
    string SystemName,
    string SystemVersion,
    string OrganizationNumber,
    string Category,
    string PeriodElement,
    string PeriodValue,
    int Year,
    string? PaymentKid,
    IReadOnlyList<MeldingLine> Lines)
{
    /// <summary>The amount to pay (negative = to be refunded): the sum of the lines.</summary>
    public long Total => Lines.Sum(l => l.Vat);
}

public sealed record MeldingIssue(string Rule, bool Blocks, string Message);

/// <summary>
/// The VAT return (mva-melding) in Skatteetaten's XML format, schema v1.0 (<c>mvaMeldingDto</c>), and the checks made before it may
/// be sent. The schema is Skatteetaten's own; the business checks are the ones of their published rules (forretningsregler) that
/// can be made from the message alone. The rules that need Skatteetaten's registers (obligations, earlier returns) are made by the
/// test environment, not here.
/// </summary>
public static class VatMelding
{
    public const string SchemaVersion = "1.0";
    public static readonly XNamespace Ns = "no:skatteetaten:fastsetting:avgift:mva:skattemeldingformerverdiavgift:v1.0";

    /// <summary>SAF-T VAT codes that are not reported in the VAT return (Skatteetaten: 0, 7, 20, 21 and 22).</summary>
    public static readonly IReadOnlySet<string> NotReported = new HashSet<string> { "0", "7", "20", "21", "22" };

    private static readonly string[] Months =
        ["januar", "februar", "mars", "april", "mai", "juni", "juli", "august", "september", "oktober", "november", "desember"];

    /// <summary>
    /// The period code for a term. Only terms that are exactly a calendar month, two months from January, three from January, six
    /// months or the year have a code; anything else cannot be reported and the user is told so.
    /// </summary>
    public static (string Element, string Value, int Year)? Period(DateOnly start, DateOnly end)
    {
        if (start.Year != end.Year || start.Day != 1 || end != new DateOnly(end.Year, end.Month, DateTime.DaysInMonth(end.Year, end.Month)))
        {
            return null;
        }

        var months = end.Month - start.Month + 1;
        return months switch
        {
            1 => ("skattleggingsperiodeMaaned", Months[start.Month - 1], start.Year),
            2 when start.Month % 2 == 1 => ("skattleggingsperiodeToMaaneder", $"{Months[start.Month - 1]}-{Months[end.Month - 1]}", start.Year),
            3 when (start.Month - 1) % 3 == 0 => ("skattleggingsperiodeTreMaaneder", $"{Months[start.Month - 1]}-{Months[end.Month - 1]}", start.Year),
            6 when start.Month is 1 or 7 => ("skattleggingsperiodeSeksMaaneder", $"{Months[start.Month - 1]}-{Months[end.Month - 1]}", start.Year),
            12 => ("skattleggingsperiodeAar", "aarlig", start.Year),
            _ => null
        };
    }

    /// <summary>The rate as the rate list writes it: no trailing zeros, comma as decimal separator.</summary>
    public static string RateText(decimal rate) => rate.ToString("0.##", CultureInfo.GetCultureInfo("nb-NO")).Replace('.', ',');

    public static long Kroner(decimal amount) => (long)Math.Round(amount, 0, MidpointRounding.AwayFromZero);

    public static XDocument Write(Melding m)
    {
        static string N(long v) => v.ToString(CultureInfo.InvariantCulture);

        var root = new XElement(Ns + "mvaMeldingDto",
            new XElement(Ns + "innsending",
                new XElement(Ns + "regnskapssystemsreferanse", m.Reference),
                new XElement(Ns + "regnskapssystem",
                    new XElement(Ns + "systemnavn", m.SystemName),
                    new XElement(Ns + "systemversjon", m.SystemVersion))),
            new XElement(Ns + "skattegrunnlagOgBeregnetSkatt",
                new XElement(Ns + "skattleggingsperiode",
                    new XElement(Ns + "periode", new XElement(Ns + m.PeriodElement, m.PeriodValue)),
                    new XElement(Ns + "aar", m.Year.ToString(CultureInfo.InvariantCulture))),
                new XElement(Ns + "fastsattMerverdiavgift", N(m.Total)),
                m.Lines.Select(l => new XElement(Ns + "mvaSpesifikasjonslinje",
                    new XElement(Ns + "mvaKode", l.Code),
                    new XElement(Ns + "mvaKodeRegnskapsystem", l.AccountingCode),
                    l.Basis is { } basis ? new XElement(Ns + "grunnlag", N(basis)) : null,
                    l.Rate is { } rate ? new XElement(Ns + "sats", rate) : null,
                    new XElement(Ns + "merverdiavgift", N(l.Vat))))),
            new XElement(Ns + "betalingsinformasjon",
                string.IsNullOrWhiteSpace(m.PaymentKid) ? null : new XElement(Ns + "kundeIdentifikasjonsnummer", m.PaymentKid)),
            new XElement(Ns + "skattepliktig", new XElement(Ns + "organisasjonsnummer", m.OrganizationNumber)),
            new XElement(Ns + "meldingskategori", m.Category));
        return new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
    }

    /// <summary>The errors of a document against Skatteetaten's schema for the VAT return, v1.0 (empty = valid).</summary>
    public static IReadOnlyList<string> SchemaErrors(XDocument document)
    {
        var errors = new List<string>();
        var schemas = new XmlSchemaSet();
        using (var xsd = Assembly.GetExecutingAssembly().GetManifestResourceStream(
                   "Norway.Schemas.no.skatteetaten.fastsetting.avgift.mva.skattemeldingformerverdiavgift.v1.0.xsd")
               ?? throw new InvalidOperationException("The VAT return schema is not embedded."))
        using (var reader = XmlReader.Create(xsd))
        {
            schemas.Add(null, reader);
        }

        document.Validate(schemas, (_, e) => errors.Add(e.Message));
        return errors;
    }

    /// <summary>Checks the message against Skatteetaten's schema and the business rules that can be made from the message alone.</summary>
    public static IReadOnlyList<MeldingIssue> Validate(Melding m, XDocument document)
    {
        var issues = new List<MeldingIssue>();

        issues.AddRange(SchemaErrors(document).Select(error => new MeldingIssue("XSD", true, $"Skjemafeil: {error}")));

        if (m.OrganizationNumber.Length != 9 || !m.OrganizationNumber.All(char.IsAsciiDigit))
        {
            issues.Add(new MeldingIssue("R000", true, "Organisasjonsnummeret må være ni sifre (Norsk oppsett)."));
        }

        // R084: amounts need lines
        if (m.Total != 0 && m.Lines.Count == 0)
        {
            issues.Add(new MeldingIssue("R084", true, "Det må sendes inn MVA-koder når det er oppgitt et beløp."));
        }

        foreach (var line in m.Lines)
        {
            var output = line.Basis is not null || line.Rate is not null;
            // R066: input lines have neither basis nor rate, output lines have both
            if (output && (line.Basis is null || line.Rate is null))
            {
                issues.Add(new MeldingIssue("R066", true, $"MVA-kode {line.Code}: utgående linjer skal ha både grunnlag og sats."));
            }

            if (output && line.Basis is { } basis && line.Rate is not null && decimal.TryParse(line.Rate.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var rate))
            {
                var expected = Kroner(basis * rate / 100m);
                if (Math.Abs(expected - line.Vat) > 1)
                {
                    issues.Add(new MeldingIssue("R019", false, $"MVA-kode {line.Code}: avgiften {line.Vat} avviker fra grunnlag × sats ({expected})."));
                }
            }
        }

        // R079: a KID must be MOD10 or MOD11
        if (!string.IsNullOrWhiteSpace(m.PaymentKid) && !Kid.IsValid(m.PaymentKid))
        {
            issues.Add(new MeldingIssue("R079", true, $"KID {m.PaymentKid} er ikke gyldig (MOD10 eller MOD11)."));
        }

        return issues;
    }

    /// <summary>
    /// The due date of the VAT return for a term: the 10th of the second month after the term ends (the term January and
    /// February is due on the 10th of April). A date on a weekend or a public holiday moves to the next working day.
    /// </summary>
    public static DateOnly DueDate(DateOnly termEnd)
    {
        var due = new DateOnly(termEnd.Year, termEnd.Month, 1).AddMonths(2).AddDays(9);
        while (due.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || NorwegianHolidays.IsHoliday(due))
        {
            due = due.AddDays(1);
        }

        return due;
    }
}

/// <summary>Norwegian public holidays (the ones a deadline cannot fall on): fixed days and those that follow Easter.</summary>
public static class NorwegianHolidays
{
    public static bool IsHoliday(DateOnly date)
    {
        if ((date.Month, date.Day) is (1, 1) or (5, 1) or (5, 17) or (12, 25) or (12, 26))
        {
            return true;
        }

        var easter = EasterSunday(date.Year);
        return new[] { -3, -2, 0, 1, 39, 49, 50 }.Any(offset => easter.AddDays(offset) == date);
    }

    /// <summary>Easter Sunday (the Gregorian computus).</summary>
    public static DateOnly EasterSunday(int year)
    {
        var a = year % 19;
        var b = year / 100;
        var c = year % 100;
        var d = b / 4;
        var e = b % 4;
        var f = (b + 8) / 25;
        var g = (b - f + 1) / 3;
        var h = (19 * a + b - d - g + 15) % 30;
        var i = c / 4;
        var k = c % 4;
        var l = (32 + 2 * e + 2 * i - h - k) % 7;
        var m = (a + 11 * h + 22 * l) / 451;
        var month = (h + l - 7 * m + 114) / 31;
        var day = (h + l - 7 * m + 114) % 31 + 1;
        return new DateOnly(year, month, day);
    }
}
