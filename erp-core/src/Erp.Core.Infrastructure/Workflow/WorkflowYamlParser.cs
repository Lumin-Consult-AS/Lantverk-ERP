using System.Globalization;
using System.Text.Json;
using Erp.Core.Domain.Data;
using Erp.Core.Domain.Workflow;

namespace Erp.Core.Infrastructure.Workflow;

/// <summary>Parses the <c>workflows</c> manifest section (YamlDotNet object graph) into <see cref="WorkflowDefinition"/>s.</summary>
public static class WorkflowYamlParser
{
    private static readonly string[] OperatorKeys = ["eq", "ne", "gt", "ge", "lt", "le", "in", "contains", "isEmpty"];

    public static WorkflowDefinition Parse(IDictionary<object, object> map)
    {
        var def = new WorkflowDefinition
        {
            Id = Str(map, "id") ?? string.Empty,
            Object = Str(map, "object") ?? string.Empty,
            Trigger = Str(map, "trigger"),
            Mode = Str(map, "mode") ?? WorkflowModes.Sequential,
            Delegation = Str(map, "delegation") ?? "none",
            OnComplete = Str(map, "onComplete"),
            OnReject = Str(map, "onReject")
        };

        if (Get(map, "steps") is IEnumerable<object> steps)
        {
            foreach (var raw in steps)
            {
                if (raw is not IDictionary<object, object> stepMap)
                {
                    throw new MetadataValidationException($"Workflow '{def.Id}': each step must be a mapping.");
                }

                def.Steps.Add(new WorkflowStepDefinition
                {
                    Name = Str(stepMap, "name") ?? string.Empty,
                    Assignee = Get(stepMap, "assignee") is IDictionary<object, object> a ? ParseAssignee(a, def.Id) : new WorkflowAssignee(),
                    When = Get(stepMap, "when") is { } when ? ParseRule(when, def.Id) : null
                });
            }
        }

        if (Get(map, "deadline") is IDictionary<object, object> deadline)
        {
            def.Deadline = new WorkflowDeadline
            {
                Days = int.TryParse(Str(deadline, "days"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) ? days : 0,
                EscalateTo = Get(deadline, "escalateTo") is IDictionary<object, object> esc ? ParseAssignee(esc, def.Id) : null
            };
        }

        return def;
    }

    private static WorkflowAssignee ParseAssignee(IDictionary<object, object> map, string workflowId)
    {
        Guid? user = null;
        if (Str(map, "user") is { } userText)
        {
            user = Guid.TryParse(userText, out var parsed)
                ? parsed
                : throw new MetadataValidationException($"Workflow '{workflowId}': assignee user '{userText}' is not a GUID.");
        }

        return new WorkflowAssignee
        {
            Role = Str(map, "role"),
            User = user,
            Rule = Str(map, "rule"),
            Dimension = Str(map, "dimension")
        };
    }

    private static WorkflowRule ParseRule(object raw, string workflowId)
    {
        if (raw is not IDictionary<object, object> map)
        {
            throw new MetadataValidationException($"Workflow '{workflowId}': 'when' must be a mapping.");
        }

        if (Get(map, "and") is IEnumerable<object> and)
        {
            return new WorkflowRule { And = and.Select(r => ParseRule(r, workflowId)).ToList() };
        }

        if (Get(map, "or") is IEnumerable<object> or)
        {
            return new WorkflowRule { Or = or.Select(r => ParseRule(r, workflowId)).ToList() };
        }

        var field = Str(map, "field")
            ?? throw new MetadataValidationException($"Workflow '{workflowId}': 'when' condition must specify 'field'.");

        // Long form: { field, operator, value }
        if (Str(map, "operator") is { } op)
        {
            return new WorkflowRule { Field = field, Operator = op, Value = ToElement(Get(map, "value")) };
        }

        // Short form: { field: amount, gt: 50000 }
        foreach (var key in OperatorKeys)
        {
            if (map.Keys.Any(k => string.Equals(k?.ToString(), key, StringComparison.OrdinalIgnoreCase)))
            {
                return new WorkflowRule { Field = field, Operator = key, Value = ToElement(Get(map, key)) };
            }
        }

        throw new MetadataValidationException($"Workflow '{workflowId}': 'when' on field '{field}' has no operator (eq, ne, gt, ge, lt, le, in, contains, isEmpty).");
    }

    private static JsonElement? ToElement(object? raw)
    {
        object? value = raw switch
        {
            null => null,
            IEnumerable<object> list => list.Select(Scalar).ToList(),
            _ => Scalar(raw)
        };

        return value is null ? null : JsonSerializer.SerializeToElement(value);
    }

    private static object? Scalar(object? raw)
    {
        if (raw is string s)
        {
            if (bool.TryParse(s, out var b))
            {
                return b;
            }

            if (decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
            {
                return d;
            }
        }

        return raw;
    }

    private static object? Get(IDictionary<object, object> map, string key) =>
        map.FirstOrDefault(kv => string.Equals(kv.Key?.ToString(), key, StringComparison.OrdinalIgnoreCase)).Value;

    private static string? Str(IDictionary<object, object> map, string key) => Get(map, key)?.ToString();
}
