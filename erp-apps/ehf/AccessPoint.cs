namespace Ehf;

/// <param name="IdempotencyKey">The same key must never lead to two deliveries (the invoice id): sending is retried when delivery of the event is.</param>
/// <param name="SenderId">The sender's PEPPOL participant id, e.g. <c>0192:955777888</c>.</param>
public sealed record OutgoingDocument(string IdempotencyKey, string SenderId, string ReceiverId, byte[] Xml, string FileName);

public sealed record ReceivedDocument(string Id, byte[] Xml);

/// <summary>
/// The PEPPOL access point. This is the seam for choosing and changing the provider: the app produces and reads the documents
/// and an implementation only moves them. An implementation throws <see cref="AccessPointException"/> with a message fit for the
/// user when it cannot.
/// </summary>
public interface IAccessPoint
{
    /// <summary>Delivers the document and returns the provider's reference for it.</summary>
    Task<string> SendAsync(OutgoingDocument document, CancellationToken cancellationToken);

    /// <summary>The documents waiting for us. They stay until <see cref="AcknowledgeAsync"/>, so reading is safe to repeat.</summary>
    Task<IReadOnlyList<ReceivedDocument>> ReceiveAsync(CancellationToken cancellationToken);

    Task AcknowledgeAsync(string id, CancellationToken cancellationToken);
}

public sealed class AccessPointException(string message) : Exception(message);

/// <summary>What runs until an access point is chosen and set up: nothing is sent or fetched, and the reason is said plainly.</summary>
public sealed class NotConfiguredAccessPoint : IAccessPoint
{
    private const string Message = "Det er ikke satt opp noe PEPPOL-aksesspunkt. EHF kan ikke sendes eller hentes før det er gjort.";

    public Task<string> SendAsync(OutgoingDocument document, CancellationToken cancellationToken) => throw new AccessPointException(Message);

    public Task<IReadOnlyList<ReceivedDocument>> ReceiveAsync(CancellationToken cancellationToken) => throw new AccessPointException(Message);

    public Task AcknowledgeAsync(string id, CancellationToken cancellationToken) => throw new AccessPointException(Message);
}
