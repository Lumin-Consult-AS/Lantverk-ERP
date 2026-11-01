using Erp.Sdk;

namespace Finance;

/// <summary>
/// Outgoing invoices (ADR-0031): a draft with lines is posted into a voucher with a receivable, then sent through an adapter that
/// answers <c>finance.salesinvoice.send.requested@1</c> (EHF today). The tax on each line comes from the line itself
/// (<c>vatAmount</c>, which an app that knows the tax rules fills in); this app only adds it up.
/// </summary>
public sealed record SalesPostRequested(Guid InvoiceId);

public sealed partial class FinanceApp
{
    private const string SalesInvoice = "finance.salesinvoice";
    private const string SalesInvoiceLine = "finance.salesinvoiceline";

    [OnValidate(SalesInvoice)]
    public async Task<ValidationResult> ValidateSalesInvoice(Record changed, Erp.Sdk.AppContext ctx)
    {
        if (changed.Get<string>("status") != "posting")
        {
            return ValidationResult.Ok;
        }

        var invoice = await ctx.Data.GetAsync(SalesInvoice, changed.Id) ?? changed;
        var failures = new List<ValidationFailure>();
        if (invoice.Get<DateOnly?>("invoiceDate") is null)
        {
            failures.Add(new ValidationFailure("invoiceDate", "Fakturaen mangler fakturadato."));
        }

        var lines = await ctx.Data.QueryAsync(SalesInvoiceLine, $"invoice eq '{invoice.Id}'", top: 1000);
        if (lines.Count == 0)
        {
            failures.Add(new ValidationFailure("lines", "Fakturaen har ingen linjer."));
        }

        if (lines.Any(l => l.Get<decimal>("amount") == 0))
        {
            failures.Add(new ValidationFailure("lines", "En linje har beløp 0."));
        }

        if (await FindReceivableAsync(ctx) is null)
        {
            failures.Add(new ValidationFailure("lines", "Det finnes ingen aktiv kundekonto (reskontro for kunder). Opprett en i kontoplanen."));
        }

        return ValidationResult.Errors(failures);
    }

    [OnAfterSave(SalesInvoice)]
    public async Task AfterSalesInvoiceSaved(Record changed, Erp.Sdk.AppContext ctx)
    {
        switch (changed.Get<string>("status"))
        {
            case "posting":
                await ctx.Events.PublishAsync("finance.salesinvoice.post.requested@1", changed.Id.ToString(), new { invoiceId = changed.Id });
                break;
            case "sending":
                await ctx.Events.PublishAsync("finance.salesinvoice.send.requested@1", changed.Id.ToString(), new { invoiceId = changed.Id });
                break;
        }
    }

    /// <summary>
    /// Makes the voucher and posts it, then marks the invoice posted. This runs as its own request rather than inside the status
    /// change, so the voucher is posted the ordinary way (with the packages that react to it). If it cannot be posted the invoice
    /// goes back to a draft with the reason. Safe to deliver again: an invoice that already has its voucher is only marked posted.
    /// </summary>
    [OnEvent("finance.salesinvoice.post.requested@1")]
    public async Task OnSalesInvoicePostRequested(SalesPostRequested e, Erp.Sdk.AppContext ctx)
    {
        var invoice = await ctx.Data.GetAsync(SalesInvoice, e.InvoiceId);
        if (invoice is null || invoice.Get<string>("status") != "posting")
        {
            return;
        }

        try
        {
            if (invoice.Get<Guid?>("voucher") is null)
            {
                await PostSalesInvoiceAsync(e.InvoiceId, ctx);
            }

            await ctx.Data.TransitionAsync(SalesInvoice, e.InvoiceId, "posted");
        }
        catch (Exception ex) when (ex is Grpc.Core.RpcException or InvalidOperationException)
        {
            var current = await ctx.Data.GetAsync(SalesInvoice, e.InvoiceId) ?? invoice;
            await ctx.Data.UpdateAsync(SalesInvoice, current.Id, current.RowVersion, new Dictionary<string, object?>
            {
                ["message"] = Truncate($"Fakturaen kunne ikke bokføres: {(ex is Grpc.Core.RpcException rpc ? rpc.Status.Detail : ex.Message)}", 2000)
            });
            await ctx.Data.TransitionAsync(SalesInvoice, current.Id, "unpost");
        }
    }

    /// <summary>A line is a quantity times a price; the amount follows. Lines can only be changed while the invoice is a draft.</summary>
    [OnValidate(SalesInvoiceLine)]
    public async Task<ValidationResult> ValidateSalesInvoiceLine(Record line, Erp.Sdk.AppContext ctx)
    {
        var invoiceId = line.Get<Guid?>("invoice") ?? (await ctx.Data.GetAsync(SalesInvoiceLine, line.Id))?.Get<Guid?>("invoice");
        if (invoiceId is { } id && await ctx.Data.GetAsync(SalesInvoice, id) is { } invoice && invoice.Get<string>("status") != "draft")
        {
            return ValidationResult.Error("invoice", "Linjene kan bare endres mens fakturaen er et utkast.");
        }

        return ValidationResult.Ok;
    }

    [OnAfterSave(SalesInvoiceLine)]
    public async Task AfterSalesInvoiceLineSaved(Record changed, Erp.Sdk.AppContext ctx)
    {
        var line = await ctx.Data.GetAsync(SalesInvoiceLine, changed.Id);
        if (line is null)
        {
            return;
        }

        var amount = Math.Round(line.Get<decimal>("quantity") * line.Get<decimal>("unitPrice"), 2, MidpointRounding.AwayFromZero);
        if (line.Get<decimal?>("amount") != amount)
        {
            await ctx.Data.UpdateAsync(SalesInvoiceLine, line.Id, line.RowVersion, new Dictionary<string, object?> { ["amount"] = amount });
        }
    }

    private static async Task<Record?> FindReceivableAsync(Erp.Sdk.AppContext ctx) =>
        (await ctx.Data.QueryAsync(Account, "controlType eq 'receivable' and isActive eq true", top: 100))
            .OrderBy(a => a.Get<string>("number"), StringComparer.Ordinal).FirstOrDefault();

    /// <summary>
    /// Makes the voucher: the receivable (with the customer and the due date) for the whole amount against the income lines. The
    /// customer item is made when the voucher is posted, and a package that knows payment references fills in the reference, which
    /// is then put on the invoice so it can be printed or sent.
    /// </summary>
    private static async Task PostSalesInvoiceAsync(Guid invoiceId, Erp.Sdk.AppContext ctx)
    {
        var invoice = await ctx.Data.GetAsync(SalesInvoice, invoiceId);
        if (invoice is null || invoice.Get<Guid?>("voucher") is not null)
        {
            return;
        }

        var receivable = await FindReceivableAsync(ctx)
            ?? throw new InvalidOperationException("Det finnes ingen aktiv kundekonto (reskontro for kunder).");
        var customerId = invoice.Get<Guid>("customer");
        var date = invoice.Get<DateOnly>("invoiceDate");
        var number = invoice.Get<string>("invoiceNumber");
        var text = Truncate($"Faktura {number}", 500);
        var voucher = await ctx.Data.CreateAsync(Voucher, new Dictionary<string, object?> { ["voucherDate"] = date, ["description"] = text });

        decimal gross = 0;
        foreach (var line in await ctx.Data.QueryAsync(SalesInvoiceLine, $"invoice eq '{invoice.Id}'", top: 1000))
        {
            var amount = line.Get<decimal>("amount");
            gross += amount + (line.Get<decimal?>("vatAmount") ?? 0m);
            var fields = new Dictionary<string, object?>
            {
                ["voucher"] = voucher.Id,
                ["account"] = line.Get<Guid>("account"),
                ["description"] = Truncate(line.Get<string>("description"), 500),
                [amount >= 0 ? "credit" : "debit"] = Math.Abs(amount)
            };
            foreach (var (name, value) in line.Fields)
            {
                if (value is not null && (DimensionFields.Contains(name) || name.Contains('.')))
                {
                    fields[name] = line.Get<object>(name);
                }
            }

            await ctx.Data.CreateAsync(Line, fields);
        }

        await ctx.Data.CreateAsync(Line, new Dictionary<string, object?>
        {
            ["voucher"] = voucher.Id,
            ["account"] = receivable.Id,
            [gross >= 0 ? "debit" : "credit"] = Math.Abs(gross),
            ["party"] = customerId,
            ["dueDate"] = invoice.Get<DateOnly?>("dueDate") ?? date,
            ["description"] = text
        });
        await ctx.Data.TransitionAsync(Voucher, voucher.Id, "post");

        var entry = (await ctx.Data.QueryAsync(Entry, $"voucher eq '{voucher.Id}'", top: 1)).Single();
        var posting = (await ctx.Data.QueryAsync(Posting, $"entry eq '{entry.Id}'", top: 1000))
            .Single(p => p.Get<Guid>("account") == receivable.Id && p.Get<Guid?>("party") == customerId);
        var item = (await ctx.Data.QueryAsync(OpenItem, $"posting eq '{posting.Id}'", top: 1)).FirstOrDefault();

        var current = (await ctx.Data.GetAsync(SalesInvoice, invoice.Id))!;
        await ctx.Data.UpdateAsync(SalesInvoice, current.Id, current.RowVersion, new Dictionary<string, object?>
        {
            ["voucher"] = voucher.Id,
            ["totalAmount"] = gross,
            ["paymentReference"] = string.IsNullOrWhiteSpace(item?.Get<string>("paymentReference")) ? null : item!.Get<string>("paymentReference")
        });
    }
}
