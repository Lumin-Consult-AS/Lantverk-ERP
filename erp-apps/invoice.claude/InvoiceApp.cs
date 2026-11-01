using Erp.Sdk;

namespace Invoice.Claude;

public sealed record InvoiceRequested(Guid InvoiceId);

/// <summary>
/// Extractor for supplier invoices (ADR-0030). It answers <c>finance.supplierinvoice.requested@1</c>: it claims the invoice, reads
/// the document with the extractor, writes the header and lines onto the invoice and proposes it for review. If it cannot read the
/// document the invoice is failed with the reason, and can be read again. Delivery is at least once, so an invoice already claimed
/// or answered is left alone.
/// </summary>
public sealed class InvoiceApp(IInvoiceExtractor extractor) : AppBase
{
    private const string InvoiceObject = "finance.supplierinvoice";
    private const string LineObject = "finance.supplierinvoiceline";

    [OnEvent("finance.supplierinvoice.requested@1")]
    public async Task OnInvoiceRequested(InvoiceRequested e, Erp.Sdk.AppContext ctx)
    {
        var invoice = await ctx.Data.GetAsync(InvoiceObject, e.InvoiceId);
        if (invoice is null || invoice.Get<string>("status") != "received" || !string.IsNullOrEmpty(invoice.Get<string>("extractor")))
        {
            return;
        }

        // The first extractor to write its name owns the invoice (a second one loses on the row version)
        try
        {
            invoice = await ctx.Data.UpdateAsync(InvoiceObject, invoice.Id, invoice.RowVersion, new Dictionary<string, object?> { ["extractor"] = extractor.Name });
        }
        catch (Grpc.Core.RpcException)
        {
            return;
        }

        try
        {
            var file = await ctx.Documents.GetDocumentAsync(invoice.Get<Guid>("file"))
                ?? throw new InvoiceExtractionException("Dokumentet finnes ikke.");
            var read = await extractor.ExtractAsync(file.Content, file.ContentType, file.FileName, CancellationToken.None);

            // The header first (the lines are coded with its date), then the lines, then the proposal
            invoice = await ctx.Data.UpdateAsync(InvoiceObject, invoice.Id, invoice.RowVersion, new Dictionary<string, object?>
            {
                ["supplierName"] = read.SupplierName,
                ["supplierOrgNumber"] = read.SupplierOrgNumber,
                ["invoiceNumber"] = read.InvoiceNumber,
                ["invoiceDate"] = read.InvoiceDate,
                ["dueDate"] = read.DueDate,
                ["currency"] = read.Currency,
                ["totalAmount"] = read.TotalAmount,
                ["vatAmount"] = read.VatAmount,
                ["paymentReference"] = read.PaymentReference,
                ["bankAccount"] = read.BankAccount,
                ["message"] = null
            });
            await ctx.Data.DeleteWhere(LineObject, $"invoice eq '{invoice.Id}'");
            foreach (var line in read.Lines)
            {
                await ctx.Data.CreateAsync(LineObject, new Dictionary<string, object?>
                {
                    ["invoice"] = invoice.Id,
                    ["description"] = line.Description,
                    ["amount"] = line.Amount,
                    ["vatRate"] = line.VatRate,
                    ["vatAmount"] = line.VatAmount
                });
            }

            await ctx.Data.TransitionAsync(InvoiceObject, invoice.Id, "propose");
        }
        catch (Exception ex) when (ex is InvoiceExtractionException or Grpc.Core.RpcException)
        {
            var current = await ctx.Data.GetAsync(InvoiceObject, e.InvoiceId) ?? invoice;
            var reason = ex is Grpc.Core.RpcException rpc ? rpc.Status.Detail : ex.Message;
            await ctx.Data.UpdateAsync(InvoiceObject, current.Id, current.RowVersion, new Dictionary<string, object?>
            {
                ["extractor"] = null,
                ["message"] = $"Fakturaen kunne ikke tolkes: {reason}"
            });
            await ctx.Data.TransitionAsync(InvoiceObject, current.Id, "fail");
        }
    }
}
