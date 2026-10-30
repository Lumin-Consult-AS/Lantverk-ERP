using System.Text.Json;
using Erp.Core.Domain.Data;
using Erp.Core.Domain.Metadata;

namespace Erp.Core.Domain.Workflow;

public static class WorkflowModes
{
    public const string Sequential = "sequential";
    public const string Parallel = "parallel";
}

/// <summary>
/// Approval flow attached to a status transition. Apps ship defaults in their manifest; the tenant owns and may
/// edit the stored copy (ADR-0010).
/// </summary>
public sealed class WorkflowDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Object { get; set; } = string.Empty;
    public string? Trigger { get; set; }
    public string Mode { get; set; } = WorkflowModes.Sequential;
    public List<WorkflowStepDefinition> Steps { get; set; } = [];
    public WorkflowDeadline? Deadline { get; set; }

    /// <summary><c>allowed</c> lets an assignee hand a task to another user; anything else disables delegation.</summary>
    public string Delegation { get; set; } = "none";

    /// <summary>Status transition executed when every step has been approved.</summary>
    public string? OnComplete { get; set; }

    /// <summary>Status transition executed when a step is rejected.</summary>
    public string? OnReject { get; set; }

    public bool AllowsDelegation => string.Equals(Delegation, "allowed", StringComparison.OrdinalIgnoreCase);
}

public sealed class WorkflowStepDefinition
{
    public string Name { get; set; } = string.Empty;
    public WorkflowAssignee Assignee { get; set; } = new();

    /// <summary>Step is only created when this rule holds for the record; no rule means always.</summary>
    public WorkflowRule? When { get; set; }
}

public sealed class WorkflowAssignee
{
    public string? Role { get; set; }
    public Guid? User { get; set; }

    /// <summary>Reserved (e.g. <c>dimension-owner</c>); not supported before dimensions exist.</summary>
    public string? Rule { get; set; }
    public string? Dimension { get; set; }
}

public sealed class WorkflowDeadline
{
    public int Days { get; set; }
    public WorkflowAssignee? EscalateTo { get; set; }
}

/// <summary>
/// JSON-friendly <c>when</c> rule: a comparison (<c>field</c>, <c>operator</c>, <c>value</c>) or a group (<c>and</c> / <c>or</c>).
/// Operators: eq, ne, gt, ge, lt, le, in, contains, isEmpty.
/// </summary>
public sealed class WorkflowRule
{
    public string? Field { get; set; }
    public string? Operator { get; set; }
    public JsonElement? Value { get; set; }
    public List<WorkflowRule>? And { get; set; }
    public List<WorkflowRule>? Or { get; set; }

    public FilterNode ToFilterNode()
    {
        if (And is { Count: > 0 })
        {
            return new FilterGroup(LogicalOperator.And, And.Select(r => r.ToFilterNode()).ToList());
        }

        if (Or is { Count: > 0 })
        {
            return new FilterGroup(LogicalOperator.Or, Or.Select(r => r.ToFilterNode()).ToList());
        }

        if (string.IsNullOrWhiteSpace(Field))
        {
            throw new InvalidOperationException("A 'when' rule must specify 'field' or 'and'/'or'.");
        }

        var op = (Operator ?? "eq").Trim().ToLowerInvariant() switch
        {
            "eq" => FilterOperator.Eq,
            "ne" => FilterOperator.Ne,
            "gt" => FilterOperator.Gt,
            "ge" => FilterOperator.Ge,
            "lt" => FilterOperator.Lt,
            "le" => FilterOperator.Le,
            "in" => FilterOperator.In,
            "contains" => FilterOperator.Contains,
            "isempty" => FilterOperator.IsEmpty,
            var other => throw new InvalidOperationException($"Unsupported 'when' operator '{other}'.")
        };

        return new FilterCondition(Field, op, ToValue(Value));
    }

    public bool Matches(IReadOnlyDictionary<string, object?> fields) =>
        WhenRuleEvaluator.Evaluate(ToFilterNode(), fields);

    private static object? ToValue(JsonElement? element)
    {
        if (element is null)
        {
            return null;
        }

        var e = element.Value;
        return e.ValueKind switch
        {
            JsonValueKind.Number => e.GetDecimal(),
            JsonValueKind.String => e.GetString(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => e.EnumerateArray().Select(x => ToValue(x)).ToList(),
            _ => null
        };
    }

    public static WorkflowRule Compare(string field, string op, object? value) => new()
    {
        Field = field,
        Operator = op,
        Value = value is null ? null : JsonSerializer.SerializeToElement(value, value.GetType(), (JsonSerializerOptions?)null)
    };
}

public static class WorkflowDefinitionValidator
{
    private static readonly HashSet<string> Operators = new(StringComparer.OrdinalIgnoreCase)
    {
        "eq", "ne", "gt", "ge", "lt", "le", "in", "contains", "isEmpty"
    };

    /// <summary>Returns human-readable problems; empty when the definition is valid. <paramref name="objectDef"/> enables status-machine checks.</summary>
    public static List<string> Validate(WorkflowDefinition def, ObjectDefinition? objectDef = null)
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(def.Id))
        {
            errors.Add("Workflow 'id' is required.");
        }

        if (string.IsNullOrWhiteSpace(def.Object))
        {
            errors.Add($"Workflow '{def.Id}': 'object' is required.");
        }

        if (!string.Equals(def.Mode, WorkflowModes.Sequential, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(def.Mode, WorkflowModes.Parallel, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Workflow '{def.Id}': 'mode' must be 'sequential' or 'parallel'.");
        }

        if (def.Steps.Count == 0)
        {
            errors.Add($"Workflow '{def.Id}': at least one step is required.");
        }

        for (var i = 0; i < def.Steps.Count; i++)
        {
            var step = def.Steps[i];
            var label = $"Workflow '{def.Id}' step {i + 1}";
            if (string.IsNullOrWhiteSpace(step.Name))
            {
                errors.Add($"{label}: 'name' is required.");
            }

            ValidateAssignee(step.Assignee, label, errors);
            if (step.When is not null)
            {
                ValidateRule(step.When, label, errors);
            }
        }

        if (def.Deadline is not null)
        {
            if (def.Deadline.Days <= 0)
            {
                errors.Add($"Workflow '{def.Id}': 'deadline.days' must be greater than 0.");
            }

            if (def.Deadline.EscalateTo is not null)
            {
                ValidateAssignee(def.Deadline.EscalateTo, $"Workflow '{def.Id}' escalation", errors);
            }
        }

        if (objectDef is not null)
        {
            var transitions = objectDef.Status?.Transitions;
            foreach (var (name, value) in new[] { ("onComplete", def.OnComplete), ("onReject", def.OnReject) })
            {
                if (!string.IsNullOrWhiteSpace(value) && (transitions is null || !transitions.ContainsKey(value)))
                {
                    errors.Add($"Workflow '{def.Id}': '{name}' refers to unknown status transition '{value}' on '{objectDef.Id}'.");
                }
            }
        }

        return errors;
    }

    private static void ValidateAssignee(WorkflowAssignee assignee, string label, List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(assignee.Rule))
        {
            errors.Add($"{label}: assignee rule '{assignee.Rule}' is not supported yet; use 'role' or 'user'.");
            return;
        }

        var count = (string.IsNullOrWhiteSpace(assignee.Role) ? 0 : 1) + (assignee.User is null ? 0 : 1);
        if (count != 1)
        {
            errors.Add($"{label}: assignee must specify exactly one of 'role' or 'user'.");
        }
    }

    private static void ValidateRule(WorkflowRule rule, string label, List<string> errors)
    {
        var groups = (rule.And is { Count: > 0 } ? 1 : 0) + (rule.Or is { Count: > 0 } ? 1 : 0);
        if (groups > 0)
        {
            if (groups > 1 || !string.IsNullOrWhiteSpace(rule.Field))
            {
                errors.Add($"{label}: a 'when' rule is either a comparison or one 'and'/'or' group.");
            }

            foreach (var child in (rule.And ?? []).Concat(rule.Or ?? []))
            {
                ValidateRule(child, label, errors);
            }

            return;
        }

        if (string.IsNullOrWhiteSpace(rule.Field))
        {
            errors.Add($"{label}: 'when' needs 'field' or an 'and'/'or' group.");
        }
        else if (!Operators.Contains(rule.Operator ?? string.Empty))
        {
            errors.Add($"{label}: 'when' operator '{rule.Operator}' is not one of eq, ne, gt, ge, lt, le, in, contains, isEmpty.");
        }
    }
}
