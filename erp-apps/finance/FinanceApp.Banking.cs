using Erp.Sdk;

namespace Finance;

/// <summary>
/// The bank contract (ADR-0028): payment batches built from due supplier items, approved through the flow engine, released by a
/// person and handed to a bank adapter by the event <c>finance.paymentbatch.released@1</c>. The adapter reports back on the batch and creates
/// bank statements and bank transactions. Nothing here knows a bank, a file format or a country.
/// </summary>
public sealed partial class FinanceApp
{
    private const string Batch = "finance.paymentbatch";
    private const string Payment = "finance.payment";
    private const string BankAccount = "finance.bankaccount";
    private const string PartyBankAccount = "finance.bankAccount";

    /// <summary>
    /// A batch that is submitted or approved must be complete: a bank account, a payment date that has not passed, payments
    /// that each have a receiver, an account to pay to and an amount, and in the bank account's currency.
    /// </summary>
    [OnValidate(Batch)]
    public async Task<ValidationResult> ValidateBatch(Record batch, Erp.Sdk.AppContext ctx)
    {
        // A batch the adapter has already answered for is not checked again (its answer is recorded on it)
        if (batch.Get<string>("status") is null or "draft" || batch.Get<string>("deliveryStatus") is not (null or "none"))
        {
            return ValidationResult.Ok;
        }

        var failures = new List<ValidationFailure>();
        var bank = batch.Get<Guid?>("bankAccount") is { } bankId ? await ctx.Data.GetAsync(BankAccount, bankId) : null;
        if (bank is null)
        {
            failures.Add(new ValidationFailure("bankAccount", "Bunten må ha en bankkonto å betale fra."));
        }

        if (batch.Get<DateOnly?>("executionDate") is not { } date || date < ctx.Today)
        {
            failures.Add(new ValidationFailure("executionDate", "Betalingsdatoen kan ikke være passert."));
        }

        var payments = await ctx.Data.QueryAsync(Payment, $"batch eq '{batch.Id}'", top: 5000);
        if (payments.Count == 0)
        {
            failures.Add(new ValidationFailure("payments", "Bunten har ingen betalinger."));
        }

        foreach (var payment in payments)
        {
            var who = payment.Get<string>("creditorName");
            if (string.IsNullOrWhiteSpace(payment.Get<string>("creditorAccount")))
            {
                failures.Add(new ValidationFailure("payments", $"Betalingen til {who} mangler mottakers konto."));
            }

            if (bank is not null && payment.Get<string>("currency") != bank.Get<string>("currency"))
            {
                failures.Add(new ValidationFailure("payments", $"Betalingen til {who} er ikke i bankkontoens valuta."));
            }
        }

        return failures.Count == 0 ? ValidationResult.Ok : ValidationResult.Errors(failures.DistinctBy(f => f.Message));
    }

    /// <summary>
    /// A new draft batch with a "due until" date is filled with a payment for every due supplier item that is not paid by
    /// another payment; a released batch is handed to the bank adapter (once: the adapter's answer is recorded on the batch).
    /// </summary>
    [OnAfterSave(Batch)]
    public async Task AfterBatchSaved(Record batch, Erp.Sdk.AppContext ctx)
    {
        switch (batch.Get<string>("status"))
        {
            case "draft":
                await FillBatchAsync(batch, ctx);
                break;
            case "released" when batch.Get<string>("deliveryStatus") is null or "none":
                await ctx.Events.PublishAsync("finance.paymentbatch.released@1", batch.Id.ToString(), new { batchId = batch.Id });
                break;
        }
    }

    /// <summary>A payment can only be changed while its batch is a draft, and an item is paid by one payment only.</summary>
    [OnValidate(Payment)]
    public async Task<ValidationResult> ValidatePayment(Record payment, Erp.Sdk.AppContext ctx)
    {
        // An update carries only what changed, so the batch is found on the stored payment
        var batchOf = payment.Get<Guid?>("batch") ?? (await ctx.Data.GetAsync(Payment, payment.Id))?.Get<Guid?>("batch");
        if (batchOf is { } batchId
            && await ctx.Data.GetAsync(Batch, batchId) is { } batch
            && batch.Get<string>("status") != "draft")
        {
            return ValidationResult.Error("batch", "Betalinger kan bare endres mens bunten er et utkast.");
        }

        return ValidationResult.Ok;
    }

    /// <summary>The batch's sum and count follow its payments.</summary>
    [OnAfterSave(Payment)]
    public async Task AfterPaymentSaved(Record payment, Erp.Sdk.AppContext ctx)
    {
        // A deleted payment arrives with no fields and is gone, so every draft batch is brought in step instead
        var batchOf = payment.Get<Guid?>("batch") ?? (await ctx.Data.GetAsync(Payment, payment.Id))?.Get<Guid?>("batch");
        if (batchOf is { } batchId)
        {
            await UpdateBatchTotalsAsync(batchId, ctx);
            return;
        }

        foreach (var draft in await ctx.Data.QueryAsync(Batch, "status eq 'draft'", top: 500))
        {
            await UpdateBatchTotalsAsync(draft.Id, ctx);
        }
    }

    private static async Task FillBatchAsync(Record batch, Erp.Sdk.AppContext ctx)
    {
        if (batch.Get<DateOnly?>("dueUntil") is not { } dueUntil
            || batch.Get<Guid?>("bankAccount") is not { } bankId
            || (await ctx.Data.QueryAsync(Payment, $"batch eq '{batch.Id}'", top: 1)).Count > 0)
        {
            return;
        }

        var bank = await ctx.Data.GetAsync(BankAccount, bankId);
        if (bank is null)
        {
            return;
        }

        var payableAccounts = (await ctx.Data.QueryAsync(Account, "controlType eq 'payable'", top: 1000)).Select(a => a.Id).ToHashSet();
        var alreadyPaid = (await ctx.Data.QueryAsync(Payment, "currency ne ''", top: 5000))
            .Select(p => p.Get<Guid?>("openItem")).Where(id => id is not null).ToHashSet();
        var due = await ctx.Data.QueryAsync(OpenItem, $"status eq 'open' and dueDate le '{dueUntil:yyyy-MM-dd}'", orderBy: "created_at", top: 5000);

        var parties = new Dictionary<Guid, Record?>();
        foreach (var item in due.Where(i => payableAccounts.Contains(i.Get<Guid>("account")) && i.Get<decimal>("remaining") < 0 && !alreadyPaid.Contains(i.Id)))
        {
            var partyId = item.Get<Guid>("party");
            if (!parties.TryGetValue(partyId, out var party))
            {
                parties[partyId] = party = await ctx.Data.GetAsync("core.party", partyId);
            }

            var posting = await ctx.Data.GetAsync(Posting, item.Get<Guid>("posting"));
            await ctx.Data.CreateAsync(Payment, new Dictionary<string, object?>
            {
                ["batch"] = batch.Id,
                ["openItem"] = item.Id,
                ["party"] = partyId,
                ["creditorName"] = party?.Get<string>("name") ?? "-",
                ["creditorAccount"] = party?.Get<string>(PartyBankAccount),
                ["amount"] = -item.Get<decimal>("remaining"),
                ["currency"] = bank.Get<string>("currency"),
                ["message"] = Truncate(posting?.Get<string>("description"), 140),
                ["dueDate"] = item.Get<DateOnly>("dueDate")
            });
        }
    }

    private static async Task UpdateBatchTotalsAsync(Guid batchId, Erp.Sdk.AppContext ctx)
    {
        var batch = await ctx.Data.GetAsync(Batch, batchId);
        if (batch is null)
        {
            return;
        }

        var payments = await ctx.Data.QueryAsync(Payment, $"batch eq '{batchId}'", top: 5000);
        var total = payments.Sum(p => p.Get<decimal>("amount"));
        if (batch.Get<decimal>("totalAmount") != total || batch.Get<int>("paymentCount") != payments.Count)
        {
            await ctx.Data.UpdateAsync(Batch, batch.Id, batch.RowVersion, new Dictionary<string, object?>
            {
                ["totalAmount"] = total,
                ["paymentCount"] = payments.Count
            });
        }
    }

    private static string? Truncate(string? text, int length) =>
        text is null ? null : text.Length <= length ? text : text[..length];
}
