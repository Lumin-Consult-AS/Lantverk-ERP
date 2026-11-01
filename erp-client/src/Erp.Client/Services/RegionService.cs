using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.Client.Models;

namespace Erp.Client.Services;

public enum DatePartOrder
{
    DayMonthYear,
    MonthDayYear,
    YearMonthDay
}

/// <summary>How a region writes dates and numbers. The client owns this; the core knows only the region code (ADR-0033).</summary>
public sealed record RegionProfile(string Code, string Label, string DateFormat, char DateSeparator, DatePartOrder Order, char DecimalSeparator, char GroupSeparator, DayOfWeek FirstDayOfWeek);

/// <summary>
/// Shows and reads dates, numbers and amounts the way the user's region writes them: <c>31.12.2026</c> and <c>1 234,56</c> in
/// Norway. It is used only at the screen: what goes to the API and comes back is always ISO 8601 dates and numbers with a decimal
/// point, and text that cannot be read in the region never leaves the client. It does not depend on the browser's culture data.
/// </summary>
public sealed class RegionService
{
    /// <summary>A no-break space, so an amount is not split over two lines.</summary>
    public const char NoBreakSpace = ' ';

    private static readonly Regex IsoDate = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex IsoDateTime = new(@"^\d{4}-\d{2}-\d{2}T", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static readonly IReadOnlyList<RegionProfile> Profiles =
    [
        new("NO", "Norge", "dd.MM.yyyy", '.', DatePartOrder.DayMonthYear, ',', NoBreakSpace, DayOfWeek.Monday),
        new("GB", "United Kingdom", "dd/MM/yyyy", '/', DatePartOrder.DayMonthYear, '.', ',', DayOfWeek.Monday),
        new("US", "United States", "MM/dd/yyyy", '/', DatePartOrder.MonthDayYear, '.', ',', DayOfWeek.Sunday)
    ];

    /// <summary>What a region without a profile gets: ISO dates, decimal point.</summary>
    public static readonly RegionProfile International =
        new("ZZ", "International", "yyyy-MM-dd", '-', DatePartOrder.YearMonthDay, '.', ',', DayOfWeek.Monday);

    public RegionProfile Current { get; private set; } = Profiles[0];

    public event Action? OnRegionChanged;

    public void SetRegion(string? code)
    {
        var profile = Profiles.FirstOrDefault(p => string.Equals(p.Code, code, StringComparison.OrdinalIgnoreCase)) ?? International;
        if (profile == Current)
        {
            return;
        }

        Current = profile;
        OnRegionChanged?.Invoke();
    }

    // ------------------------------------------------------------------ showing

    public string FormatDate(DateOnly date) => date.ToString(Current.DateFormat, CultureInfo.InvariantCulture);

    public string FormatDateTime(DateTimeOffset value, TimeZoneInfo? zone = null)
    {
        var local = zone is null ? value.ToLocalTime() : TimeZoneInfo.ConvertTime(value, zone);
        return $"{FormatDate(DateOnly.FromDateTime(local.DateTime))} {local:HH:mm}";
    }

    /// <param name="scale">Number of decimals, or null to show the decimals the number has (at most six).</param>
    public string FormatNumber(decimal value, int? scale = null)
    {
        var decimals = scale ?? Math.Min(6, (decimal.GetBits(value)[3] >> 16) & 0xFF);
        if (scale is null)
        {
            value = decimal.Round(value, decimals);
            decimals = Math.Min(decimals, TrimmedDecimals(value));
        }

        var text = Math.Abs(value).ToString("F" + decimals, CultureInfo.InvariantCulture);
        var point = text.IndexOf('.');
        var whole = point < 0 ? text : text[..point];
        var fraction = point < 0 ? string.Empty : text[(point + 1)..];

        var grouped = new StringBuilder();
        for (var i = 0; i < whole.Length; i++)
        {
            if (i > 0 && (whole.Length - i) % 3 == 0)
            {
                grouped.Append(Current.GroupSeparator);
            }

            grouped.Append(whole[i]);
        }

        var sign = value < 0 ? "-" : string.Empty;
        return fraction.Length == 0 ? sign + grouped : sign + grouped + Current.DecimalSeparator + fraction;
    }

    /// <summary>A whole number; four digits are left together (a year, a code), longer numbers are grouped in threes.</summary>
    public string FormatInteger(long value) =>
        Math.Abs(value) < 10_000 ? value.ToString(CultureInfo.InvariantCulture) : FormatNumber(value, 0);

    private static int TrimmedDecimals(decimal value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        var point = text.IndexOf('.');
        return point < 0 ? 0 : text[(point + 1)..].TrimEnd('0').Length;
    }

    /// <summary>
    /// A value as the screen shows it, by the type of its field (or, when the field is not known, by what the value looks like).
    /// Anything that is not a date or a number is shown as it is.
    /// </summary>
    public string Display(ClientFieldDefinition? field, object? raw)
    {
        var text = I18nService.UnwrapDisplayString(raw);
        if (text.Length == 0)
        {
            return text;
        }

        var type = field?.Type ?? Infer(raw, text);
        switch (type)
        {
            case ClientFieldType.Date when DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date):
                return FormatDate(date);
            case ClientFieldType.DateTime when DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var moment):
                return FormatDateTime(moment);
            case ClientFieldType.Decimal when decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number):
                return FormatNumber(number, field?.Scale);
            case ClientFieldType.Integer when long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole):
                return FormatInteger(whole);
            case ClientFieldType.Money when raw is JsonElement { ValueKind: JsonValueKind.Object } money
                                            && money.TryGetProperty("amount", out var amount) && money.TryGetProperty("currency", out var currency)
                                            && decimal.TryParse(amount.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value):
                return $"{FormatNumber(value, 2)} {currency.GetString()}";
            default:
                return text;
        }
    }

    private static ClientFieldType Infer(object? raw, string text) => raw switch
    {
        JsonElement { ValueKind: JsonValueKind.Number } element => element.TryGetInt64(out _) ? ClientFieldType.Integer : ClientFieldType.Decimal,
        decimal or double or float => ClientFieldType.Decimal,
        int or long => ClientFieldType.Integer,
        _ when IsoDate.IsMatch(text) => ClientFieldType.Date,
        _ when IsoDateTime.IsMatch(text) => ClientFieldType.DateTime,
        _ => ClientFieldType.String
    };

    // ------------------------------------------------------------------ reading

    /// <summary>
    /// Reads a date typed by the user: in the region's own form (<c>31.12.2026</c>, <c>31.12.26</c>, <c>1.2.2026</c>), short
    /// (<c>311226</c>, <c>31122026</c>) or as ISO 8601 (<c>2026-12-31</c>).
    /// </summary>
    public bool TryParseDate(string? input, out DateOnly date)
    {
        date = default;
        var text = input?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return false;
        }

        if (IsoDate.IsMatch(text))
        {
            return DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }

        string[] parts;
        if (text.All(char.IsAsciiDigit))
        {
            parts = text.Length switch
            {
                6 => [text[..2], text[2..4], text[4..]],
                8 when Current.Order == DatePartOrder.YearMonthDay => [text[..4], text[4..6], text[6..]],
                8 => [text[..2], text[2..4], text[4..]],
                _ => []
            };
        }
        else
        {
            parts = text.Split(['.', '/', '-', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (parts.Length != 3 || parts.Any(p => p.Length == 0 || !p.All(char.IsAsciiDigit)))
        {
            return false;
        }

        // A year in front is always ISO order, whatever the region
        var yearFirst = Current.Order == DatePartOrder.YearMonthDay || parts[0].Length == 4;
        var first = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var second = int.Parse(parts[1], CultureInfo.InvariantCulture);
        var third = int.Parse(parts[2], CultureInfo.InvariantCulture);
        int year, month, day;
        if (yearFirst)
        {
            (year, month, day) = (first, second, third);
        }
        else if (Current.Order == DatePartOrder.MonthDayYear)
        {
            (month, day, year) = (first, second, third);
        }
        else
        {
            (day, month, year) = (first, second, third);
        }

        var yearText = yearFirst ? parts[0] : parts[2];
        if (yearText.Length == 2)
        {
            year += 2000;
        }
        else if (yearText.Length != 4)
        {
            return false;
        }

        if (year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        date = new DateOnly(year, month, day);
        return true;
    }

    /// <summary>
    /// Reads a number typed by the user. Spaces are ignored. The region's decimal separator is the decimal point; with both a
    /// comma and a point the last one is the decimal point; a single point in Norway is read as a decimal point too, since
    /// that is what people type (<c>12.5</c>).
    /// </summary>
    public bool TryParseNumber(string? input, out decimal value)
    {
        value = 0;
        var text = new string((input ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != ' ').ToArray()).Replace('−', '-');
        if (text.Length == 0)
        {
            return false;
        }

        var sign = string.Empty;
        if (text[0] is '-' or '+')
        {
            sign = text[0] == '-' ? "-" : string.Empty;
            text = text[1..];
        }

        if (text.Length == 0 || !text.All(c => char.IsAsciiDigit(c) || c is ',' or '.'))
        {
            return false;
        }

        var lastComma = text.LastIndexOf(',');
        var lastPoint = text.LastIndexOf('.');
        char? decimalMark;
        if (lastComma >= 0 && lastPoint >= 0)
        {
            decimalMark = lastComma > lastPoint ? ',' : '.';
        }
        else if (lastComma >= 0 || lastPoint >= 0)
        {
            var mark = lastComma >= 0 ? ',' : '.';
            var count = text.Count(c => c == mark);
            var digitsAfter = text.Length - text.LastIndexOf(mark) - 1;
            if (count > 1)
            {
                // Repeated, it can only be a grouping mark; the region's own decimal separator cannot repeat
                if (mark == Current.DecimalSeparator)
                {
                    return false;
                }

                decimalMark = null;
            }
            else if (mark == Current.DecimalSeparator)
            {
                decimalMark = mark;
            }
            else if (mark == Current.GroupSeparator && digitsAfter == 3)
            {
                decimalMark = null;
            }
            else
            {
                decimalMark = mark;
            }
        }
        else
        {
            decimalMark = null;
        }

        var whole = text;
        var fraction = string.Empty;
        if (decimalMark is { } point)
        {
            var at = text.LastIndexOf(point);
            whole = text[..at];
            fraction = text[(at + 1)..];
            if (fraction.Any(c => c is ',' or '.'))
            {
                return false;
            }
        }

        // What is left in the whole part must be grouping done the same way throughout: 1 234 567, or 1,234,567
        var marks = whole.Where(c => c is ',' or '.').Distinct().ToList();
        if (marks.Count > 1)
        {
            return false;
        }

        if (marks.Count == 1)
        {
            var groups = whole.Split(marks[0]);
            if (groups[0].Length is < 1 or > 3 || groups.Skip(1).Any(g => g.Length != 3))
            {
                return false;
            }
        }

        var digits = sign + whole.Replace(",", string.Empty).Replace(".", string.Empty) + (fraction.Length > 0 ? "." + fraction : string.Empty);
        return decimal.TryParse(digits, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>The ISO 8601 form that goes to the API.</summary>
    public static string ToIso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
