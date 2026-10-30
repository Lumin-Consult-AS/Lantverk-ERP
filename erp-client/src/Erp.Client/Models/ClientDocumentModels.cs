namespace Erp.Client.Models;

public sealed class ClientInboxItem
{
    public Guid Id { get; set; }
    public Guid InboxId { get; set; }
    public string Channel { get; set; } = string.Empty;
    public string Sender { get; set; } = string.Empty;
    public string? SenderName { get; set; }
    public string Subject { get; set; } = string.Empty;
    public string? BodyText { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public List<Guid> FileIds { get; set; } = [];
    public List<Guid> DuplicateFileIds { get; set; } = [];
    public string? ClaimedBy { get; set; }
    public string? RejectReason { get; set; }
}

public sealed class ClientStoredFile
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string ScanStatus { get; set; } = string.Empty;
}

public sealed class ClientInboxItemDetail
{
    public ClientInboxItem Item { get; set; } = new();
    public List<ClientStoredFile> Files { get; set; } = [];
}
