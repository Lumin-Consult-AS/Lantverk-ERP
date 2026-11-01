namespace Invoice.Claude;

/// <summary>
/// What an extractor reads from a supplier invoice document. This is the seam for swapping the AI supplier: another provider
/// (Mindee, Azure Document Intelligence) is another implementation of <see cref="IInvoiceExtractor"/>, or, when it should run
/// as its own app, another app that answers the same event and fills the same fields.
/// </summary>
public sealed record ExtractedInvoice(
    string? SupplierName,
    string? SupplierOrgNumber,
    string? InvoiceNumber,
    DateOnly? InvoiceDate,
    DateOnly? DueDate,
    string? Currency,
    decimal? TotalAmount,
    decimal? VatAmount,
    string? PaymentReference,
    string? BankAccount,
    IReadOnlyList<ExtractedLine> Lines);

/// <param name="Amount">Net amount, without VAT. Negative on a credit note.</param>
public sealed record ExtractedLine(string? Description, decimal Amount, decimal? VatRate, decimal? VatAmount);

public interface IInvoiceExtractor
{
    /// <summary>A name for the "read by" field, e.g. <c>claude-eu</c>.</summary>
    string Name { get; }

    /// <summary>Reads the document. Throws <see cref="InvoiceExtractionException"/> when it cannot be read; the message is shown to the user.</summary>
    Task<ExtractedInvoice> ExtractAsync(byte[] content, string contentType, string fileName, CancellationToken cancellationToken);
}

public sealed class InvoiceExtractionException(string message) : Exception(message);
