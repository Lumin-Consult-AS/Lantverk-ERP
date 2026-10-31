namespace Erp.Core.Domain.Metadata;

/// <summary>A gap-free number series for a string field: <c>{prefix}{year-}{number padded}</c>.</summary>
public sealed class NumberSeriesDefinition
{
    public string Prefix { get; set; } = string.Empty;
    public int Padding { get; set; }

    /// <summary>The counter starts again at 1 each calendar year, and the year is part of the value.</summary>
    public bool PerYear { get; set; }

    public string Format(long number, int year)
    {
        var digits = Padding > 0 ? number.ToString().PadLeft(Padding, '0') : number.ToString();
        return PerYear ? $"{Prefix}{year}-{digits}" : $"{Prefix}{digits}";
    }
}
