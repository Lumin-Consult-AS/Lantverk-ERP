using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Erp.Client.Models;

namespace Erp.Client.Services;

/// <summary>
/// Mirrors object metadata validation rules on the client (<c>required</c>, <c>requiredWhen</c>,
/// <c>readOnlyWhen</c>, <c>visibleWhen</c>, <c>maxLength</c>, <c>minValue</c>/<c>maxValue</c>, <c>pattern</c>).
/// Authoritative enforcement always takes place in the core Data API.
/// </summary>
public static class ClientFieldValidator
{
    public static Dictionary<string, string> ValidateRecord(
        ClientObjectDefinition objectDef,
        IReadOnlyDictionary<string, object?> fieldValues,
        I18nService? i18n = null)
    {
        var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in objectDef.Fields)
        {
            var err = ValidateField(field, objectDef.OwnerApp, fieldValues, i18n);
            if (!string.IsNullOrEmpty(err))
            {
                var exposedName = field.GetExposedName(objectDef.OwnerApp);
                errors[exposedName] = err;
            }
        }
        return errors;
    }

    public static string? ValidateField(
        ClientFieldDefinition field,
        string objectOwnerApp,
        IReadOnlyDictionary<string, object?> fieldValues,
        I18nService? i18n = null)
    {
        if (!IsFieldVisible(field, fieldValues))
        {
            return null;
        }

        var exposedName = field.GetExposedName(objectOwnerApp);
        var label = i18n?.T(field.Label, field.Label ?? field.Name) ?? field.Label ?? field.Name;

        fieldValues.TryGetValue(exposedName, out var rawVal);
        if (rawVal is null && !string.Equals(exposedName, field.Name, StringComparison.OrdinalIgnoreCase))
        {
            fieldValues.TryGetValue(field.Name, out rawVal);
        }

        var isEmpty = IsValueEmpty(rawVal);
        var isRequired = IsFieldRequired(field, fieldValues);

        if (isEmpty)
        {
            if (isRequired && field.DefaultValue is null)
            {
                return i18n?.Format("ui.validation.required", label)
                    ?? $"Feltet '{label}' er påkrevd.";
            }
            return null;
        }

        var strVal = UnwrapToString(rawVal);

        if (field.MaxLength.HasValue && strVal.Length > field.MaxLength.Value)
        {
            return i18n?.Format("ui.validation.maxLength", label, field.MaxLength.Value)
                ?? $"Feltet '{label}' kan maks være {field.MaxLength.Value} tegn.";
        }

        if (!string.IsNullOrWhiteSpace(field.Pattern) && !string.IsNullOrEmpty(strVal))
        {
            if (!Regex.IsMatch(strVal, field.Pattern, RegexOptions.CultureInvariant))
            {
                return i18n?.Format("ui.validation.pattern", label, field.Pattern)
                    ?? $"Feltet '{label}' har ugyldig format ({field.Pattern}).";
            }
        }

        if (TryConvertToDecimal(rawVal, out var numVal))
        {
            if (field.MinValue.HasValue && numVal < field.MinValue.Value)
            {
                return i18n?.Format("ui.validation.min", label, field.MinValue.Value)
                    ?? $"Feltet '{label}' må være minst {field.MinValue.Value}.";
            }

            if (field.MaxValue.HasValue && numVal > field.MaxValue.Value)
            {
                return i18n?.Format("ui.validation.max", label, field.MaxValue.Value)
                    ?? $"Feltet '{label}' kan maks være {field.MaxValue.Value}.";
            }
        }

        return null;
    }

    public static bool IsFieldRequired(ClientFieldDefinition field, IReadOnlyDictionary<string, object?> fieldValues)
        => field.Required || (field.RequiredWhen is not null && EvaluateWhen(field.RequiredWhen, fieldValues));

    public static bool IsFieldReadOnly(ClientFieldDefinition field, IReadOnlyDictionary<string, object?> fieldValues)
        => field.ReadOnlyWhen is not null && EvaluateWhen(field.ReadOnlyWhen, fieldValues);

    public static bool IsFieldVisible(ClientFieldDefinition field, IReadOnlyDictionary<string, object?> fieldValues)
        => field.VisibleWhen is null || EvaluateWhen(field.VisibleWhen, fieldValues);

    public static bool EvaluateWhen(ClientFilterNode? rule, IReadOnlyDictionary<string, object?> fields)
    {
        if (rule is null)
        {
            return false;
        }

        return rule switch
        {
            ClientFilterCondition cond => EvaluateCondition(cond, fields),
            ClientFilterGroup grp => grp.Logical == ClientLogicalOperator.And
                ? grp.Clauses.All(c => EvaluateWhen(c, fields))
                : grp.Clauses.Any(c => EvaluateWhen(c, fields)),
            _ => false
        };
    }

    private static bool EvaluateCondition(ClientFilterCondition cond, IReadOnlyDictionary<string, object?> fields)
    {
        fields.TryGetValue(cond.Field, out var rawActual);
        var actual = UnwrapScalar(rawActual);
        var expected = UnwrapScalar(cond.Value);

        if (cond.Operator == ClientFilterOperator.IsEmpty)
        {
            var empty = IsValueEmpty(actual);
            var expBool = expected switch
            {
                null => true,
                bool b => b,
                string s when bool.TryParse(s, out var parsed) => parsed,
                _ => true
            };
            return empty == expBool;
        }

        if (cond.Operator == ClientFilterOperator.In)
        {
            if (actual is null) return false;
            if (expected is IEnumerable list and not string)
            {
                foreach (var item in list)
                {
                    if (ValuesEqual(actual, UnwrapScalar(item))) return true;
                }
                return false;
            }
            return ValuesEqual(actual, expected);
        }

        if (cond.Operator == ClientFilterOperator.Contains)
        {
            if (actual is null || expected is null) return false;
            return UnwrapToString(actual).Contains(UnwrapToString(expected), StringComparison.OrdinalIgnoreCase);
        }

        return cond.Operator switch
        {
            ClientFilterOperator.Eq => ValuesEqual(actual, expected),
            ClientFilterOperator.Ne => !ValuesEqual(actual, expected),
            ClientFilterOperator.Gt => CompareValues(actual, expected) is > 0,
            ClientFilterOperator.Ge => CompareValues(actual, expected) is >= 0,
            ClientFilterOperator.Lt => CompareValues(actual, expected) is < 0,
            ClientFilterOperator.Le => CompareValues(actual, expected) is <= 0,
            _ => false
        };
    }

    public static bool IsValueEmpty(object? value)
    {
        var unwrapped = UnwrapScalar(value);
        return unwrapped switch
        {
            null => true,
            string s => string.IsNullOrWhiteSpace(s),
            _ => false
        };
    }

    private static object? UnwrapScalar(object? value)
    {
        if (value is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => je.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => je.TryGetDecimal(out var d) ? d : je.GetDouble(),
                JsonValueKind.Array => je.EnumerateArray().Select(e => UnwrapScalar(e)).ToList(),
                _ => je.ToString()
            };
        }
        return value;
    }

    private static string UnwrapToString(object? value)
        => Convert.ToString(UnwrapScalar(value), CultureInfo.InvariantCulture) ?? string.Empty;

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left is null && right is null) return true;
        if (left is null || right is null) return false;

        if (TryConvertToDecimal(left, out var ld) && TryConvertToDecimal(right, out var rd))
        {
            return ld == rd;
        }

        return string.Equals(UnwrapToString(left), UnwrapToString(right), StringComparison.OrdinalIgnoreCase);
    }

    private static int? CompareValues(object? left, object? right)
    {
        if (left is null || right is null) return null;
        if (TryConvertToDecimal(left, out var ld) && TryConvertToDecimal(right, out var rd))
        {
            return ld.CompareTo(rd);
        }
        return string.Compare(UnwrapToString(left), UnwrapToString(right), StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryConvertToDecimal(object? value, out decimal result)
    {
        var unwrapped = UnwrapScalar(value);
        switch (unwrapped)
        {
            case decimal d: result = d; return true;
            case int i: result = i; return true;
            case long l: result = l; return true;
            case double db: result = (decimal)db; return true;
            case float f: result = (decimal)f; return true;
            case string s when decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p):
                result = p;
                return true;
            default:
                result = 0m;
                return false;
        }
    }
}

