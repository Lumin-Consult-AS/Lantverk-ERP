using System.Text.RegularExpressions;

namespace Erp.Core.Domain.Metadata;

public enum FieldType
{
    String,
    Text,
    Integer,
    Decimal,
    Boolean,
    Date,
    DateTime,
    Uuid,
    Reference,
    Enum,
    Json,
    Money,
    Blob
}

public enum RelationDeleteBehavior
{
    Restrict,
    Cascade,
    SetNull
}

public static partial class IdentifierNaming
{
    [GeneratedRegex("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidSqlIdentifierRegex();

    [GeneratedRegex("([a-z0-9])([A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex CamelToSnakeRegex();

    public static string ToSnakeCase(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("Identifier cannot be empty.", nameof(input));
        }

        var normalized = input.Trim().Replace('.', '_').Replace('-', '_');
        var snake = CamelToSnakeRegex().Replace(normalized, "$1_$2").ToLowerInvariant();
        if (!ValidSqlIdentifierRegex().IsMatch(snake))
        {
            throw new ArgumentException($"Invalid SQL identifier '{input}' (normalized: '{snake}').", nameof(input));
        }

        return snake;
    }

    public static (string Schema, string Table) SplitObjectId(string objectId)
    {
        if (string.IsNullOrWhiteSpace(objectId))
        {
            throw new ArgumentException("Object ID cannot be empty.", nameof(objectId));
        }

        var parts = objectId.Trim().Split('.', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2)
        {
            throw new ArgumentException($"Object ID '{objectId}' must have the format '<app>.<object>'.", nameof(objectId));
        }

        return (ToSnakeCase(parts[0]), ToSnakeCase(parts[1]));
    }
}

