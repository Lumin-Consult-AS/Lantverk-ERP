using Erp.Sdk;

namespace Ehf;

public sealed record SendRequested(Guid InvoiceId);

/// <summary>
/// EHF in and out (ADR-0031). Out: a posted sales invoice that is to be sent becomes a PEPPOL BIS Billing 3.0 document, which is
/// kept as a file on the invoice and handed to the access point. In: documents waiting at the access point become supplier
/// invoices in <c>finance</c> already read (they are structured, so the invoice reader is not involved) and are proposed for review
/// like any other.
/// </summary>
public sealed class EhfApp(IAccessPoint accessPoint) : AppBase
{
    public const string Reader = "ehf";

    private const string SalesInvoice = "finance.salesinvoice";
    private const string SalesLine = "finance.salesinvoiceline";
    private const string SupplierInvoice = "finance.supplierinvoice";
    private const string SupplierLine = "finance.supplierinvoiceline";
    private const string NorwaySettings = "norway.settings";
    private const string VatRate = "norway.vatrate";

    // ------------------------------------------------------------------ sending

    /// <summary>
    /// Makes the document, checks it, and hands it to the access point. Delivery of the event is at least once, so the invoice id is
    /// the idempotency key given to the access point, and an invoice that already has a reference is only marked delivered.
    /// </summary>
    [OnEvent("finance.salesinvoice.send.requested@1")]
    public async Task OnSendRequested(SendRequested e, Erp.Sdk.AppContext ctx)
    {
        var invoice = await ctx.Data.GetAsync(SalesInvoice, e.InvoiceId);
        if (invoice is null || invoice.Get<string>("status") != "sending")
        {
            return;
        }

        try
        {
            if (string.IsNullOrEmpty(invoice.Get<string>("deliveryReference")))
            {
                var document = await BuildAsync(invoice, ctx);
                var xml = System.Text.Encoding.UTF8.GetBytes(document.Declaration + Environment.NewLine + document.Root);
                var fileName = $"ehf_{invoice.Get<string>("invoiceNumber")}.xml";
                var file = await ctx.Documents.StoreDocumentAsync(fileName, "application/xml", xml);

                var customer = (await ctx.Data.GetAsync("core.party", invoice.Get<Guid>("customer")))!;
                var seller = (await ctx.Data.QueryAsync(NorwaySettings, top: 1)).First();
                var reference = await accessPoint.SendAsync(new OutgoingDocument(
                    invoice.Id.ToString("N"),
                    Ubl.ParticipantId(seller.Get<string>("organizationNumber")!),
                    Ubl.ParticipantId(customer.Get<string>("organizationNumber")!),
                    xml,
                    fileName), CancellationToken.None);

                invoice = await ctx.Data.UpdateAsync(SalesInvoice, invoice.Id, invoice.RowVersion, new Dictionary<string, object?>
                {
                    ["file"] = file,
                    ["deliveryReference"] = reference,
                    ["deliveryMessage"] = "Sendt som EHF (PEPPOL BIS Billing 3.0)."
                });
            }

            await ctx.Data.TransitionAsync(SalesInvoice, invoice.Id, "delivered");
        }
        catch (Exception ex) when (ex is EhfException or AccessPointException or Grpc.Core.RpcException)
        {
            var current = await ctx.Data.GetAsync(SalesInvoice, e.InvoiceId) ?? invoice;
            await ctx.Data.UpdateAsync(SalesInvoice, current.Id, current.RowVersion, new Dictionary<string, object?>
            {
                ["deliveryMessage"] = ex is Grpc.Core.RpcException rpc ? rpc.Status.Detail : ex.Message
            });
            await ctx.Data.TransitionAsync(SalesInvoice, current.Id, "fail");
        }
    }

    private static async Task<System.Xml.Linq.XDocument> BuildAsync(Record invoice, Erp.Sdk.AppContext ctx)
    {
        var settings = (await ctx.Data.QueryAsync(NorwaySettings, top: 1)).FirstOrDefault()
            ?? throw new EhfException("Norsk oppsett mangler (firmanavn, organisasjonsnummer og adresse).");
        var customer = await ctx.Data.GetAsync("core.party", invoice.Get<Guid>("customer"))
            ?? throw new EhfException("Kunden finnes ikke.");
        var bank = (await ctx.Data.QueryAsync("finance.bankaccount", top: 1)).FirstOrDefault();
        var currency = (await ctx.Data.QueryAsync("finance.settings", top: 1)).FirstOrDefault()?.Get<string>("baseCurrency") ?? "NOK";

        var lines = (await ctx.Data.QueryAsync(SalesLine, $"invoice eq '{invoice.Id}'", orderBy: "created_at", top: 1000))
            .Select(l => new EhfLine(
                l.Get<string>("description") ?? "-",
                l.Get<decimal>("quantity"),
                l.Get<decimal>("unitPrice"),
                l.Get<decimal>("amount"),
                l.Get<decimal?>("vatRate")))
            .ToList();

        var model = new EhfInvoice(
            invoice.Get<string>("invoiceNumber") ?? string.Empty,
            invoice.Get<DateOnly>("invoiceDate"),
            invoice.Get<DateOnly?>("dueDate"),
            currency,
            string.IsNullOrWhiteSpace(invoice.Get<string>("buyerReference")) ? customer.Get<string>("partyNumber") ?? string.Empty : invoice.Get<string>("buyerReference")!,
            invoice.Get<string>("paymentReference"),
            bank?.Get<string>("accountNumber"),
            new EhfParty(
                settings.Get<string>("companyName") ?? string.Empty,
                settings.Get<string>("organizationNumber") ?? string.Empty,
                settings.Get<string>("street"),
                settings.Get<string>("postalCode"),
                settings.Get<string>("city"),
                settings.Get<string>("email"),
                settings.Get<bool>("vatRegistered")),
            new EhfParty(customer.Get<string>("name") ?? string.Empty, customer.Get<string>("organizationNumber") ?? string.Empty, null, null, null, customer.Get<string>("email"), false),
            lines);

        var document = Ubl.Write(model);
        var gross = Ubl.Totals(lines).Gross;
        if (gross != invoice.Get<decimal>("totalAmount"))
        {
            throw new EhfException($"Summen i EHF ({gross:0.00}) er ikke den samme som den bokførte ({invoice.Get<decimal>("totalAmount"):0.00}). Fakturaen er ikke sendt.");
        }

        return document;
    }

    // ------------------------------------------------------------------ receiving

    [Command("ehf.receive")]
    public async Task<CommandResult> Receive(CommandContext cmd, Erp.Sdk.AppContext ctx)
    {
        IReadOnlyList<ReceivedDocument> waiting;
        try
        {
            waiting = await accessPoint.ReceiveAsync(CancellationToken.None);
        }
        catch (AccessPointException ex)
        {
            return CommandResult.Adjusted(ex.Message);
        }

        int made = 0, known = 0, failed = 0;
        foreach (var document in waiting)
        {
            var file = await ctx.Documents.StoreDocumentAsync($"ehf_{document.Id}.xml", "application/xml", document.Xml);
            if ((await ctx.Data.QueryAsync(SupplierInvoice, $"file eq '{file}'", top: 1)).Count > 0)
            {
                known++;
            }
            else if (await MakeInvoiceAsync(file, document, ctx))
            {
                made++;
            }
            else
            {
                failed++;
            }

            // Only once it is ours (an invoice, even a failed one, exists) does the access point forget it
            await accessPoint.AcknowledgeAsync(document.Id, CancellationToken.None);
        }

        return CommandResult.Applied($"{made} nye fakturaer fra EHF, {known} var hentet før, {failed} kunne ikke leses.");
    }

    private static async Task<bool> MakeInvoiceAsync(Guid file, ReceivedDocument document, Erp.Sdk.AppContext ctx)
    {
        ReceivedInvoice read;
        try
        {
            read = Ubl.Read(document.Xml);
        }
        catch (EhfException ex)
        {
            var broken = await ctx.Data.CreateAsync(SupplierInvoice, new Dictionary<string, object?> { ["file"] = file, ["extractor"] = Reader });
            await ctx.Data.UpdateAsync(SupplierInvoice, broken.Id, broken.RowVersion, new Dictionary<string, object?> { ["message"] = $"EHF-dokumentet kunne ikke leses: {ex.Message}" });
            await ctx.Data.TransitionAsync(SupplierInvoice, broken.Id, "fail");
            return false;
        }

        // The reader name is set from the start, so the invoice reader leaves it alone
        var invoice = await ctx.Data.CreateAsync(SupplierInvoice, new Dictionary<string, object?>
        {
            ["file"] = file,
            ["extractor"] = Reader,
            ["supplierName"] = Truncate(read.SupplierName, 200),
            ["supplierOrgNumber"] = read.SupplierOrgNumber,
            ["invoiceNumber"] = Truncate(read.InvoiceNumber, 50),
            ["invoiceDate"] = read.InvoiceDate,
            ["dueDate"] = read.DueDate,
            ["currency"] = read.Currency,
            ["totalAmount"] = read.TotalAmount,
            ["vatAmount"] = read.VatAmount,
            ["paymentReference"] = Truncate(read.PaymentReference, 25),
            ["bankAccount"] = read.BankAccount?.Replace(" ", string.Empty)
        });
        foreach (var line in read.Lines)
        {
            await ctx.Data.CreateAsync(SupplierLine, new Dictionary<string, object?>
            {
                ["invoice"] = invoice.Id,
                ["description"] = Truncate(line.Description, 500),
                ["amount"] = line.Amount,
                ["vatRate"] = line.VatRate,
                ["vatAmount"] = line.VatAmount
            });
        }

        await ctx.Data.TransitionAsync(SupplierInvoice, invoice.Id, "propose");
        return true;
    }

    private static string? Truncate(string? text, int length) => text is null || text.Length <= length ? text : text[..length];
}
