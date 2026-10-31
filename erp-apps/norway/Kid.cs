namespace Norway;

/// <summary>
/// KID, the Norwegian payment reference: 2 to 25 digits, the last of which is a check digit by MOD10 (Luhn: weights 2 and 1 from
/// the right) or MOD11 (weights 2 to 7 from the right). We make KIDs with MOD10; both kinds are accepted when checking.
/// </summary>
public static class Kid
{
    public const int MinLength = 2;
    public const int MaxLength = 25;

    /// <summary>The number with its MOD10 check digit appended.</summary>
    public static string WithMod10(string number) => number + Mod10Digit(number);

    /// <summary>The number with its MOD11 check digit appended, or null when the number has no valid MOD11 digit.</summary>
    public static string? WithMod11(string number) => Mod11Digit(number) is { } digit ? number + digit : null;

    public static bool IsValid(string? kid)
    {
        if (kid is null || kid.Length < MinLength || kid.Length > MaxLength || !kid.All(char.IsAsciiDigit))
        {
            return false;
        }

        var body = kid[..^1];
        var check = kid[^1];
        return Mod10Digit(body) == check || Mod11Digit(body) == check;
    }

    private static char Mod10Digit(string number)
    {
        var sum = 0;
        var weight = 2;
        for (var i = number.Length - 1; i >= 0; i--)
        {
            var product = (number[i] - '0') * weight;
            sum += product > 9 ? product - 9 : product;
            weight = weight == 2 ? 1 : 2;
        }

        return (char)('0' + (10 - sum % 10) % 10);
    }

    private static char? Mod11Digit(string number)
    {
        var sum = 0;
        var weight = 2;
        for (var i = number.Length - 1; i >= 0; i--)
        {
            sum += (number[i] - '0') * weight;
            weight = weight == 7 ? 2 : weight + 1;
        }

        var digit = 11 - sum % 11;
        return digit switch { 11 => '0', 10 => null, _ => (char)('0' + digit) };
    }
}
