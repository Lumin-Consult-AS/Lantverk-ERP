using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace Erp.Core.Domain.Data;

/// <summary>
/// Evaluates declarative <see cref="FilterNode"/> (<c>when</c>) rules in-memory against a record's field values.
/// Used by the core data API for <c>requiredWhen</c> / <c>readOnlyWhen</c> and by workflow step conditions.
/// </summary>
public static class WhenRuleEvaluator
{
    public static bool Evaluate(FilterNode? rule, IReadOnlyDictionary<string, object?> fields)
    {
        if (rule is null)
        {
            return false;
        }

        return rule switch
        {
            FilterCondition condition => EvaluateCondition(condition, fields),
            FilterGroup group => EvaluateGroup(group, fields),
            _ => false
        };
    }

    private static bool EvaluateGroup(FilterGroup group, IReadOnlyDictionary<string, object?> fields)
    {
        if (group.Clauses.Count == 0)
        {
            return true;
        }

        return group.Logical switch
        {
            LogicalOperator.And => group.Clauses.All(c => Evaluate(c, fields)),
            LogicalOperator.Or => group.Clauses.Any(c => Evaluate(c, fields)),
            _ => false
        };
    }

    private static bool EvaluateCondition(FilterCondition condition, IReadOnlyDictionary<string, object?> fields)
    {
        var rawActual = ResolveFieldValue(condition.Field, fields);
        var actual = UnwrapValue(rawActual);
        var expected = UnwrapValue(condition.Value);

        if (condition.Operator == FilterOperator.IsEmpty)
        {
            var isEmpty = IsValueEmpty(actual);
            var expectedBool = expected switch
            {
                null => true,
                bool b => b,
                string s when bool.TryParse(s, out var parsed) => parsed,
                _ => true
            };
            return isEmpty == expectedBool;
        }

        if (condition.Operator == FilterOperator.In)
        {
            if (actual is null)
            {
                return false;
            }

            if (expected is IEnumerable enumerable and not string)
            {
                foreach (var item in enumerable)
                {
                    if (ValuesEqual(actual, UnwrapValue(item)))
                    {
                        return true;
                    }
                }
                return false;
            }

            return ValuesEqual(actual, expected);
        }

        if (condition.Operator == FilterOperator.Contains)
        {
            if (actual is null || expected is null)
            {
                return false;
            }

            return Convert.ToString(actual, CultureInfo.InvariantCulture)?
                .Contains(Convert.ToString(expected, CultureInfo.InvariantCulture) ?? string.Empty, StringComparison.OrdinalIgnoreCase) == true;
        }

        return condition.Operator switch
        {
            FilterOperator.Eq => ValuesEqual(actual, expected),
            FilterOperator.Ne => !ValuesEqual(actual, expected),
            FilterOperator.Gt => CompareValues(actual, expected) is > 0,
            FilterOperator.Ge => CompareValues(actual, expected) is >= 0,
            FilterOperator.Lt => CompareValues(actual, expected) is < 0,
            FilterOperator.Le => CompareValues(actual, expected) is <= 0,
            _ => false
        };
    }

    public static bool IsValueEmpty(object? value)
    {
        var unwrapped = UnwrapValue(value);
        return unwrapped switch
        {
            null => true,
            string s => string.IsNullOrWhiteSpace(s),
            ICollection c => c.Count == 0,
            _ => false
        };
    }

    private static object? ResolveFieldValue(string fieldName, IReadOnlyDictionary<string, object?> fields)
    {
        if (fields.TryGetValue(fieldName, out var exact))
        {
            return exact;
        }

        foreach (var kvp in fields)
        {
            if (string.Equals(kvp.Key, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }

        return null;
    }

    private static object? UnwrapValue(object? value)
    {
        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => element.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => element.TryGetDecimal(out var d) ? d : element.GetDouble(),
                JsonValueKind.Array => element.EnumerateArray().Select(e => UnwrapValue(e)).ToList(),
                JsonValueKind.Object when element.TryGetProperty("amount", out var amtProp) && amtProp.TryGetDecimal(out var amt) => amt,
                _ => element.GetRawText()
            };
        }

        if (value is MoneyValue money)
        {
            return money.Amount;
        }

        return value;
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left is null && right is null)
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        if (TryConvertToDecimal(left, out var leftDec) && TryConvertToDecimal(right, out var rightDec))
        {
            return leftDec == rightDec;
        }

        if (left is bool lb && right is string rs && bool.TryParse(rs, out var rParsed))
        {
            return lb == rParsed;
        }

        if (right is bool rb && left is string ls && bool.TryParse(ls, out var lParsed))
        {
            return lParsed == rb;
        }

        var leftStr = Convert.ToString(left, CultureInfo.InvariantCulture);
        var rightStr = Convert.ToString(right, CultureInfo.InvariantCulture);
        return string.Equals(leftStr, rightStr, StringComparison.OrdinalIgnoreCase);
    }

    private static int? CompareValues(object? left, object? right)
    {
        if (left is null || right is null)
        {
            return null;
        }

        if (TryConvertToDecimal(left, out var leftDec) && TryConvertToDecimal(right, out var rightDec))
        {
            return leftDec.CompareTo(rightDec);
        }

        var leftStr = Convert.ToString(left, CultureInfo.InvariantCulture);
        var rightStr = Convert.ToString(right, CultureInfo.InvariantCulture);
        if (DateTimeOffset.TryParse(leftStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var leftDto)
            && DateTimeOffset.TryParse(rightStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var rightDto))
        {
            return leftDto.CompareTo(rightDto);
        }

        return string.Compare(leftStr, rightStr, StringComparison.OrdinalIgnoreCase);
    }

    public static bool TryConvertToDecimal(object? value, out decimal result)
    {
        var unwrapped = UnwrapValue(value);
        switch (unwrapped)
        {
            case decimal d:
                result = d;
                return true;
            case int i:
                result = i;
                return true;
            case long l:
                result = l;
                return true;
            case double db:
                result = (decimal)db;
                return true;
            case float f:
                result = (decimal)f;
                return true;
            case string s when decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed):
                result = parsed;
                return true;
            default:
                result = 0m;
                return false;
        }
    }
}

