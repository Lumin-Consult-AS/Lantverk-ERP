using System.Globalization;
using Erp.Sdk;

namespace Finance;

public sealed record InboxReceived(Guid ItemId, List<Guid>? FileIds);

/// <summary>
/// Supplier invoices (ADR-0030). A file in the inbox becomes a <c>finance.supplierinvoice</c>; an extractor app (any app that
/// answers <c>finance.supplierinvoice.requested@1</c>) fills it in from the document; this app proposes the supplier and the
/// coding from earlier posted invoices, a flow approves, and a person posts it. Nothing is posted automatically.
/// </summary>
public sealed partial class FinanceApp
{
    private const string SupplierInvoice = "finance.supplierinvoice";
    private const string SupplierInvoiceLine = "finance.supplierinvoiceline";
    private const string InboxClaimant = "finance.supplierinvoice";
    private const string PartyObject = "core.party";

    private static readonly string[] InvoiceContentTypes = ["application/pdf", "image/png", "image/jpeg"];

    // ------------------------------------------------------------------ from the inbox

    /// <summary>
    /// Takes an inbox item that carries a PDF or an image and makes one invoice per such file. Items with other content are left
    /// for the manual work list. Safe to deliver again: a file that already has an invoice gets no second one.
    /// </summary>
    [OnEvent("core.inbox.received@1")]
    public async Task OnInboxReceived(InboxReceived e, Erp.Sdk.AppContext ctx)
    {
        var invoiceFiles = new List<Guid>();
        foreach (var fileId in e.FileIds ?? [])
        {
            if (await ctx.Documents.GetDocumentAsync(fileId) is { } file
                && InvoiceContentTypes.Contains(file.ContentType, StringComparer.OrdinalIgnoreCase))
            {
                invoiceFiles.Add(fileId);
            }
        }

        if (invoiceFiles.Count == 0)
        {
            return;
        }

        try
        {
            await ctx.Documents.ClaimInboxItemAsync(e.ItemId, InboxClaimant);
        }
        catch (Grpc.Core.RpcException ex) when (ex.StatusCode is Grpc.Core.StatusCode.Aborted or Grpc.Core.StatusCode.NotFound)
        {
            return;
        }

        Guid? first = null;
        foreach (var fileId in invoiceFiles)
        {
            var existing = (await ctx.Data.QueryAsync(SupplierInvoice, $"file eq '{fileId}'", top: 1)).FirstOrDefault();
            first ??= (existing ?? await ctx.Data.CreateAsync(SupplierInvoice, new Dictionary<string, object?>
            {
                ["file"] = fileId, ["inboxItem"] = e.ItemId
            })).Id;
        }

        await ctx.Documents.CompleteInboxItemAsync(e.ItemId, InboxClaimant, SupplierInvoice, first);
    }

    // ------------------------------------------------------------------ the invoice

    [OnValidate(SupplierInvoice)]
    public async Task<ValidationResult> ValidateSupplierInvoice(Record changed, Erp.Sdk.AppContext ctx)
    {
        if (changed.Get<string>("status") != "pending")
        {
            return ValidationResult.Ok;
        }

        var invoice = await ctx.Data.GetAsync(SupplierInvoice, changed.Id) ?? changed;
        var failures = new List<ValidationFailure>();

        var supplierId = invoice.Get<Guid?>("supplier");
        Record? supplier = null;
        if (supplierId is null)
        {
            failures.Add(new ValidationFailure("supplier", "Velg leverandør før fakturaen sendes til godkjenning."));
        }
        else
        {
            supplier = await ctx.Data.GetAsync(PartyObject, supplierId.Value);
        }

        var total = invoice.Get<decimal?>("totalAmount");
        if (total is null or 0)
        {
            failures.Add(new ValidationFailure("totalAmount", "Fakturaen mangler totalbeløp."));
        }

        if (invoice.Get<DateOnly?>("invoiceDate") is null)
        {
            failures.Add(new ValidationFailure("invoiceDate", "Fakturaen mangler fakturadato."));
        }

        if (invoice.Get<string>("currency") is { Length: > 0 } currency && await GetBaseCurrencyAsync(ctx) is { } baseCurrency && currency != baseCurrency)
        {
            failures.Add(new ValidationFailure("currency", "Fakturaer i fremmed valuta kan ikke bokføres fra fakturatolken ennå. Lag bilaget for hånd."));
        }

        var number = invoice.Get<string>("invoiceNumber");
        if (supplierId is not null && !string.IsNullOrWhiteSpace(number))
        {
            var same = await ctx.Data.QueryAsync(SupplierInvoice, $"supplier eq '{supplierId}' and invoiceNumber eq '{number!.Replace("'", "''")}'", top: 20);
            if (same.Any(i => i.Id != invoice.Id && i.Get<string>("status") is not ("rejected" or "failed")))
            {
                failures.Add(new ValidationFailure("invoiceNumber", $"Faktura {number} fra denne leverandøren er registrert fra før."));
            }
        }

        // A bank account that differs from the supplier's registered one is the classic sign of invoice fraud: a person must settle it
        if (supplier?.Get<string>(PartyBankAccount) is { Length: > 0 } registered
            && invoice.Get<string>("bankAccount") is { Length: > 0 } onInvoice
            && Digits(registered) != Digits(onInvoice))
        {
            failures.Add(new ValidationFailure("bankAccount",
                $"Kontonummeret på fakturaen ({onInvoice}) er et annet enn det registrerte hos leverandøren ({registered}). Kontroller med leverandøren, og oppdater leverandøren eller tøm feltet her."));
        }

        var lines = await ctx.Data.QueryAsync(SupplierInvoiceLine, $"invoice eq '{invoice.Id}'", top: 1000);
        if (lines.Count == 0)
        {
            failures.Add(new ValidationFailure("lines", "Fakturaen har ingen linjer."));
        }
        else
        {
            if (lines.Any(l => l.Get<Guid?>("account") is null))
            {
                failures.Add(new ValidationFailure("lines", "Alle linjer må ha konto."));
            }

            var sum = lines.Sum(l => l.Get<decimal>("amount") + (l.Get<decimal?>("vatAmount") ?? 0m));
            if (total is { } t && sum != t)
            {
                failures.Add(new ValidationFailure("totalAmount",
                    string.Create(CultureInfo.InvariantCulture, $"Linjene (med skatt og avgift) summerer til {sum:0.00}, men fakturaen er på {t:0.00}.")));
            }
        }

        return ValidationResult.Errors(failures);
    }

    [OnAfterSave(SupplierInvoice)]
    public async Task AfterSupplierInvoiceSaved(Record changed, Erp.Sdk.AppContext ctx)
    {
        // An update carries only what changed, so only a change of status (or a new invoice) is acted on
        switch (changed.Get<string>("status"))
        {
            case "received":
                await ctx.Events.PublishAsync("finance.supplierinvoice.requested@1", changed.Id.ToString(), new { invoiceId = changed.Id });
                break;
            case "proposed":
                await ProposeAsync(changed.Id, ctx);
                break;
            case "posted":
                await PostInvoiceAsync(changed.Id, ctx);
                break;
        }
    }

    /// <summary>The lines of an invoice can only be changed while it is being read or reviewed, never once it is in the flow.</summary>
    [OnValidate(SupplierInvoiceLine)]
    public async Task<ValidationResult> ValidateSupplierInvoiceLine(Record line, Erp.Sdk.AppContext ctx)
    {
        var invoiceId = line.Get<Guid?>("invoice") ?? (await ctx.Data.GetAsync(SupplierInvoiceLine, line.Id))?.Get<Guid?>("invoice");
        if (invoiceId is { } id
            && await ctx.Data.GetAsync(SupplierInvoice, id) is { } invoice
            && invoice.Get<string>("status") is not ("received" or "proposed"))
        {
            return ValidationResult.Error("invoice", "Linjene kan bare endres før fakturaen sendes til godkjenning.");
        }

        return ValidationResult.Ok;
    }

    // ------------------------------------------------------------------ proposals

    /// <summary>
    /// Finds the supplier (by organisation number, then by name) and codes the lines like the supplier's latest posted invoice.
    /// Only empty fields are filled, so the extractor's and a person's values are never overwritten.
    /// </summary>
    private static async Task ProposeAsync(Guid invoiceId, Erp.Sdk.AppContext ctx)
    {
        var invoice = await ctx.Data.GetAsync(SupplierInvoice, invoiceId);
        if (invoice is null)
        {
            return;
        }

        var supplierId = invoice.Get<Guid?>("supplier") ?? await FindSupplierAsync(invoice, ctx);
        if (supplierId is not null && invoice.Get<Guid?>("supplier") is null)
        {
            await ctx.Data.UpdateAsync(SupplierInvoice, invoice.Id, invoice.RowVersion, new Dictionary<string, object?> { ["supplier"] = supplierId });
        }

        if (supplierId is null)
        {
            return;
        }

        var previous = (await ctx.Data.QueryAsync(SupplierInvoice, $"supplier eq '{supplierId}' and status eq 'posted'", orderBy: "created_at", top: 200))
            .LastOrDefault(i => i.Id != invoice.Id);
        if (previous is null)
        {
            return;
        }

        var template = (await ctx.Data.QueryAsync(SupplierInvoiceLine, $"invoice eq '{previous.Id}'", top: 1000))
            .OrderByDescending(l => Math.Abs(l.Get<decimal>("amount"))).FirstOrDefault();
        if (template is null)
        {
            return;
        }

        foreach (var line in await ctx.Data.QueryAsync(SupplierInvoiceLine, $"invoice eq '{invoice.Id}'", top: 1000))
        {
            var fill = new Dictionary<string, object?>();
            if (line.Get<Guid?>("account") is null && template.Get<Guid?>("account") is { } account)
            {
                fill["account"] = account;
                fill["suggestedAccount"] = account;
            }

            // Dimensions, and fields other apps add to the line (a VAT code from the Norway package, say), follow the earlier invoice
            foreach (var (name, value) in template.Fields)
            {
                var learned = DimensionFields.Contains(name) || name.Contains('.');
                if (learned && value is not null && line.Get<object>(name) is null)
                {
                    fill[name] = template.Get<object>(name);
                }
            }

            if (fill.Count > 0)
            {
                await ctx.Data.UpdateAsync(SupplierInvoiceLine, line.Id, line.RowVersion, fill);
            }
        }
    }

    private static async Task<Guid?> FindSupplierAsync(Record invoice, Erp.Sdk.AppContext ctx)
    {
        var org = Digits(invoice.Get<string>("supplierOrgNumber"));
        if (org.Length >= 9)
        {
            var byOrg = await ctx.Data.QueryAsync(PartyObject, $"organizationNumber eq '{org}'", top: 2);
            if (byOrg.Count == 1)
            {
                return byOrg[0].Id;
            }
        }

        if (invoice.Get<string>("supplierName") is { Length: > 0 } name)
        {
            var wanted = name.Trim();
            var byName = (await ctx.Data.QueryAsync(PartyObject, "name ne ''", top: 5000))
                .Where(p => string.Equals(p.Get<string>("name")?.Trim(), wanted, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
            if (byName.Count == 1)
            {
                return byName[0].Id;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ posting

    /// <summary>
    /// Makes the voucher from the approved invoice, posts it, and puts the invoice's payment reference on the supplier item so the
    /// payment file carries it. A voucher that cannot be posted (closed period, no payables account) refuses the invoice's own post.
    /// </summary>
    private static async Task PostInvoiceAsync(Guid invoiceId, Erp.Sdk.AppContext ctx)
    {
        var invoice = await ctx.Data.GetAsync(SupplierInvoice, invoiceId);
        if (invoice is null || invoice.Get<Guid?>("voucher") is not null)
        {
            return;
        }

        var supplierId = invoice.Get<Guid>("supplier");
        var payable = (await ctx.Data.QueryAsync(Account, "controlType eq 'payable' and isActive eq true", top: 100))
            .OrderBy(a => a.Get<string>("number"), StringComparer.Ordinal).FirstOrDefault()
            ?? throw new InvalidOperationException("Det finnes ingen aktiv leverandørkonto (reskontro for leverandører). Opprett en i kontoplanen.");

        var text = Truncate($"{invoice.Get<string>("supplierName")} {invoice.Get<string>("invoiceNumber")}".Trim(), 500);
        var date = invoice.Get<DateOnly>("invoiceDate");
        var total = invoice.Get<decimal>("totalAmount");
        var voucher = await ctx.Data.CreateAsync(Voucher, new Dictionary<string, object?> { ["voucherDate"] = date, ["description"] = text });

        foreach (var line in await ctx.Data.QueryAsync(SupplierInvoiceLine, $"invoice eq '{invoice.Id}'", top: 1000))
        {
            var amount = line.Get<decimal>("amount");
            var fields = new Dictionary<string, object?>
            {
                ["voucher"] = voucher.Id,
                ["account"] = line.Get<Guid>("account"),
                ["description"] = Truncate(line.Get<string>("description"), 500) ?? text,
                [amount >= 0 ? "debit" : "credit"] = Math.Abs(amount)
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
            ["account"] = payable.Id,
            [total >= 0 ? "credit" : "debit"] = Math.Abs(total),
            ["party"] = supplierId,
            ["dueDate"] = invoice.Get<DateOnly?>("dueDate") ?? date,
            ["description"] = text
        });
        await ctx.Data.TransitionAsync(Voucher, voucher.Id, "post");

        var entry = (await ctx.Data.QueryAsync(Entry, $"voucher eq '{voucher.Id}'", top: 1)).Single();
        var posting = (await ctx.Data.QueryAsync(Posting, $"entry eq '{entry.Id}'", top: 1000))
            .Single(p => p.Get<Guid>("account") == payable.Id && p.Get<Guid?>("party") == supplierId);
        if (invoice.Get<string>("paymentReference") is { Length: > 0 } reference
            && (await ctx.Data.QueryAsync(OpenItem, $"posting eq '{posting.Id}'", top: 1)).FirstOrDefault() is { } item)
        {
            await ctx.Data.UpdateAsync(OpenItem, item.Id, item.RowVersion, new Dictionary<string, object?> { ["paymentReference"] = reference });
        }

        // A supplier without a registered account takes the one on the approved invoice
        if (invoice.Get<string>("bankAccount") is { Length: > 0 } account
            && await ctx.Data.GetAsync(PartyObject, supplierId) is { } party
            && string.IsNullOrWhiteSpace(party.Get<string>(PartyBankAccount)))
        {
            await ctx.Data.UpdateAsync(PartyObject, party.Id, party.RowVersion, new Dictionary<string, object?> { [PartyBankAccount] = account });
        }

        var current = (await ctx.Data.GetAsync(SupplierInvoice, invoice.Id))!;
        await ctx.Data.UpdateAsync(SupplierInvoice, current.Id, current.RowVersion, new Dictionary<string, object?> { ["voucher"] = voucher.Id });
    }

    private static string Digits(string? text) => new((text ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
}
