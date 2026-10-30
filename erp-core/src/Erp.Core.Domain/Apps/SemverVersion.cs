using System.Globalization;
using System.Text.RegularExpressions;

namespace Erp.Core.Domain.Apps;

/// <summary>
/// Represents a Semantic Version (<c>MAJOR.MINOR.PATCH</c>) with optional pre-release tag.
/// </summary>
public readonly record struct SemverVersion(int Major, int Minor, int Patch, string? PreRelease = null)
    : IComparable<SemverVersion>
{
    private static readonly Regex SemverRegex = new(
        @"^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)(?:\.(?<patch>0|[1-9]\d*))?(?:-(?<pre>[0-9A-Za-z\-\.]+))?(?:\+(?<build>[0-9A-Za-z\-\.]+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryParse(string? input, out SemverVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var match = SemverRegex.Match(input.Trim());
        if (!match.Success)
        {
            return false;
        }

        var major = int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture);
        var minor = int.Parse(match.Groups["minor"].Value, CultureInfo.InvariantCulture);
        var patch = match.Groups["patch"].Success
            ? int.Parse(match.Groups["patch"].Value, CultureInfo.InvariantCulture)
            : 0;
        var pre = match.Groups["pre"].Success ? match.Groups["pre"].Value : null;

        version = new SemverVersion(major, minor, patch, pre);
        return true;
    }

    public static SemverVersion Parse(string input)
        => TryParse(input, out var v)
            ? v
            : throw new FormatException($"Invalid semantic version '{input}'.");

    public int CompareTo(SemverVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;

        if (PreRelease is null && other.PreRelease is null) return 0;
        if (PreRelease is null) return 1; // Normal release > pre-release
        if (other.PreRelease is null) return -1;
        return string.Compare(PreRelease, other.PreRelease, StringComparison.Ordinal);
    }

    public static bool operator <(SemverVersion left, SemverVersion right) => left.CompareTo(right) < 0;
    public static bool operator <=(SemverVersion left, SemverVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >(SemverVersion left, SemverVersion right) => left.CompareTo(right) > 0;
    public static bool operator >=(SemverVersion left, SemverVersion right) => left.CompareTo(right) >= 0;

    public override string ToString()
        => PreRelease is null ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{PreRelease}";
}

/// <summary>
/// Evaluates semantic version range expressions such as <c>&gt;=1.0 &lt;2.0</c>, <c>^1.0.0</c>, or <c>1.2.0</c>.
/// </summary>
public sealed class SemverRange
{
    private readonly List<Func<SemverVersion, bool>> _predicates = [];
    public string RawExpression { get; }

    private SemverRange(string rawExpression, List<Func<SemverVersion, bool>> predicates)
    {
        RawExpression = rawExpression;
        _predicates = predicates;
    }

    public static bool TryParse(string? expression, out SemverRange? range)
    {
        range = null;
        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        var trimmed = expression.Trim();
        if (trimmed == "*")
        {
            range = new SemverRange(trimmed, [_ => true]);
            return true;
        }

        var tokens = trimmed.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var predicates = new List<Func<SemverVersion, bool>>();

        foreach (var token in tokens)
        {
            if (token.StartsWith(">=", StringComparison.Ordinal))
            {
                if (!SemverVersion.TryParse(token[2..], out var target)) return false;
                predicates.Add(v => v >= target);
            }
            else if (token.StartsWith("<=", StringComparison.Ordinal))
            {
                if (!SemverVersion.TryParse(token[2..], out var target)) return false;
                predicates.Add(v => v <= target);
            }
            else if (token.StartsWith(">", StringComparison.Ordinal))
            {
                if (!SemverVersion.TryParse(token[1..], out var target)) return false;
                predicates.Add(v => v > target);
            }
            else if (token.StartsWith("<", StringComparison.Ordinal))
            {
                if (!SemverVersion.TryParse(token[1..], out var target)) return false;
                predicates.Add(v => v < target);
            }
            else if (token.StartsWith("^", StringComparison.Ordinal))
            {
                if (!SemverVersion.TryParse(token[1..], out var target)) return false;
                var upper = new SemverVersion(target.Major + 1, 0, 0);
                predicates.Add(v => v >= target && v < upper);
            }
            else if (token.StartsWith("~", StringComparison.Ordinal))
            {
                if (!SemverVersion.TryParse(token[1..], out var target)) return false;
                var upper = new SemverVersion(target.Major, target.Minor + 1, 0);
                predicates.Add(v => v >= target && v < upper);
            }
            else if (token.StartsWith("=", StringComparison.Ordinal))
            {
                if (!SemverVersion.TryParse(token[1..], out var target)) return false;
                predicates.Add(v => v == target);
            }
            else
            {
                if (!SemverVersion.TryParse(token, out var target)) return false;
                predicates.Add(v => v == target);
            }
        }

        if (predicates.Count == 0)
        {
            return false;
        }

        range = new SemverRange(trimmed, predicates);
        return true;
    }

    public static SemverRange Parse(string expression)
        => TryParse(expression, out var r) && r is not null
            ? r
            : throw new FormatException($"Invalid semantic version range '{expression}'.");

    public bool IsSatisfiedBy(SemverVersion version)
        => _predicates.All(p => p(version));

    public bool IsSatisfiedBy(string versionString)
        => SemverVersion.TryParse(versionString, out var v) && IsSatisfiedBy(v);
}

