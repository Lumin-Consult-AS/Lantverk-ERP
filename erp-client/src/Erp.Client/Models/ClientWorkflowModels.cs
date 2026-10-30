using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Erp.Client.Models;

public sealed class ClientTask
{
    public Guid Id { get; set; }
    public Guid WorkflowInstanceId { get; set; }
    public int StepIndex { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ObjectId { get; set; } = string.Empty;
    public Guid RecordId { get; set; }
    public string Link { get; set; } = string.Empty;
    public Guid? AssigneeUser { get; set; }
    public string? AssigneeRole { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset? DueAt { get; set; }
    public bool Escalated { get; set; }
    public Guid? DelegatedFrom { get; set; }
    public string? Comment { get; set; }
}

public sealed class ClientAttentionItem
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Severity { get; set; } = "info";
    public int Count { get; set; }
    public string? OwnerApp { get; set; }
    public string? Page { get; set; }
    public bool Hidden { get; set; }
    public DateTimeOffset? SnoozedUntil { get; set; }
}

public sealed class ClientWorkflowEntry
{
    public ClientWorkflowDefinition Definition { get; set; } = new();
    public string? SourceApp { get; set; }
    public bool IsCustomized { get; set; }
}

public sealed class ClientWorkflowDefinition
{
    public string Id { get; set; } = string.Empty;
    public string Object { get; set; } = string.Empty;
    public string? Trigger { get; set; }
    public string Mode { get; set; } = "sequential";
    public string Delegation { get; set; } = "none";
    public string? OnComplete { get; set; }
    public string? OnReject { get; set; }
    public ClientWorkflowDeadline? Deadline { get; set; }
    public List<ClientWorkflowStep> Steps { get; set; } = [];
}

public sealed class ClientWorkflowStep
{
    public string Name { get; set; } = string.Empty;
    public ClientWorkflowAssignee Assignee { get; set; } = new();
    public ClientWorkflowRule? When { get; set; }
}

public sealed class ClientWorkflowAssignee
{
    public string? Role { get; set; }
    public Guid? User { get; set; }
    public string? Rule { get; set; }
    public string? Dimension { get; set; }
}

public sealed class ClientWorkflowDeadline
{
    public int Days { get; set; }
    public ClientWorkflowAssignee? EscalateTo { get; set; }
}

/// <summary>
/// A step's <c>when</c> rule. The editor changes a single comparison (<c>field</c> / <c>operator</c> / <c>value</c>);
/// grouped rules (<c>and</c> / <c>or</c>) are kept as they are so editing a flow never drops them.
/// </summary>
public sealed class ClientWorkflowRule
{
    public string? Field { get; set; }
    public string? Operator { get; set; }
    public JsonElement? Value { get; set; }
    public List<ClientWorkflowRule>? And { get; set; }
    public List<ClientWorkflowRule>? Or { get; set; }

    [JsonIgnore]
    public bool IsGroup => And is { Count: > 0 } || Or is { Count: > 0 };

    [JsonIgnore]
    public string ValueText
    {
        get => Value is null ? string.Empty : Value.Value.ValueKind == JsonValueKind.String ? Value.Value.GetString() ?? string.Empty : Value.Value.ToString();
        set
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                Value = null;
            }
            else if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var number))
            {
                Value = JsonSerializer.SerializeToElement(number);
            }
            else if (bool.TryParse(value, out var flag))
            {
                Value = JsonSerializer.SerializeToElement(flag);
            }
            else
            {
                Value = JsonSerializer.SerializeToElement(value);
            }
        }
    }
}
