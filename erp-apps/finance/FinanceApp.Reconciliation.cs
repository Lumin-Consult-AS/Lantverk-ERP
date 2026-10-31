using Erp.Sdk;

namespace Finance;

public sealed record BankTransactionCreated(Guid RecordId);

/// <summary>
/// Bank reconciliation (ADR-0029). A new bank transaction is matched to an open item: a payment in by the reference the payer
/// quoted, a payment out by the end-to-end id of the payment it settles. When reference and amount agree, the transaction is
/// accepted at once: the receipt or payment is posted against the bank account and the open item is settled, in one step. All
/// else goes to review with a suggestion where there is one. Nothing here knows a country or a reference format.
/// </summary>
public sealed partial class FinanceApp
{
    private const string BankTransaction = "finance.banktransaction";
    private const string BankStatement = "finance.bankstatement";
    private const string BankReconciliation = "finance.bankreconciliation";

    private sealed record Match(Record? Item, bool Exact, string Note);

    // ------------------------------------------------------------------ matching

    [OnEvent("finance.banktransaction.created@1")]
    public async Task OnBankTransactionCreated(BankTransactionCreated e, Erp.Sdk.AppContext ctx)
    {
        var tx = await ctx.Data.GetAsync(BankTransaction, e.RecordId);
        if (tx is null || tx.Get<string>("status") != "new")
        {
            return;
        }

        var match = await FindMatchAsync(tx, ctx);
        await ctx.Data.UpdateAsync(BankTransaction, tx.Id, tx.RowVersion, new Dictionary<string, object?>
        {
            ["suggestedOpenItem"] = match.Item?.Id,
            ["note"] = match.Note
        });
        await ctx.Data.TransitionAsync(BankTransaction, tx.Id, "suggest");

        if (match.Exact)
        {
            try
            {
                await ctx.Data.TransitionAsync(BankTransaction, tx.Id, "accept");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // It stays in review, with the reason, for a person to settle
                var current = await ctx.Data.GetAsync(BankTransaction, tx.Id);
                if (current is not null)
                {
                    await ctx.Data.UpdateAsync(BankTransaction, current.Id, current.RowVersion, new Dictionary<string, object?>
                    {
                        ["note"] = Truncate($"Kunne ikke avstemmes automatisk: {(ex is Grpc.Core.RpcException rpc ? rpc.Status.Detail : ex.Message)}", 500)
                    });
                }
            }
        }
    }

    private static async Task<Match> FindMatchAsync(Record tx, Erp.Sdk.AppContext ctx)
    {
        var amount = tx.Get<decimal>("amount");
        var control = amount > 0 ? "receivable" : "payable";
        var accounts = (await ctx.Data.QueryAsync(Account, $"controlType eq '{control}'", top: 1000)).Select(a => a.Id).ToHashSet();
        bool Open(Record item) => accounts.Contains(item.Get<Guid>("account")) && item.Get<string>("status") == "open"
                                  && Math.Sign(item.Get<decimal>("remaining")) == Math.Sign(amount);

        if (amount > 0)
        {
            if (tx.Get<string>("paymentReference") is { Length: > 0 } reference)
            {
                var byReference = (await ctx.Data.QueryAsync(OpenItem, $"paymentReference eq '{reference.Replace("'", "''")}'", top: 20)).Where(Open).ToList();
                if (byReference.Count == 1)
                {
                    var item = byReference[0];
                    return item.Get<decimal>("remaining") == amount
                        ? new Match(item, true, $"Referanse {reference} og beløp stemmer.")
                        : new Match(item, false, $"Referanse {reference} peker på en post med åpent beløp {item.Get<decimal>("remaining"):0.00}, men innbetalt er {amount:0.00}.");
                }

                if (byReference.Count > 1)
                {
                    return new Match(null, false, $"Referanse {reference} finnes på flere åpne poster.");
                }
            }
        }
        else if (Guid.TryParse(tx.Get<string>("endToEndId"), out var paymentId) && await ctx.Data.GetAsync(Payment, paymentId) is { } payment
                 && payment.Get<Guid?>("openItem") is { } payableId && await ctx.Data.GetAsync(OpenItem, payableId) is { } payable && Open(payable))
        {
            return payable.Get<decimal>("remaining") == amount
                ? new Match(payable, true, "Betalingen fra betalingsbunten stemmer med bankens melding.")
                : new Match(payable, false, $"Betalingen gjelder en post med åpent beløp {payable.Get<decimal>("remaining"):0.00}, men banken meldte {amount:0.00}.");
        }

        // No reference to go by: an item with exactly this amount is a suggestion, never an automatic match
        var sameAmount = (await ctx.Data.QueryAsync(OpenItem, "status eq 'open'", orderBy: "created_at", top: 5000))
            .Where(i => Open(i) && i.Get<decimal>("remaining") == amount).ToList();
        return sameAmount.Count switch
        {
            1 => new Match(sameAmount[0], false, "Foreslått ut fra beløpet; ingen referanse stemte."),
            > 1 => new Match(null, false, $"{sameAmount.Count} åpne poster har dette beløpet; velg riktig post."),
            _ => new Match(null, false, "Ingen treff.")
        };
    }

    // ------------------------------------------------------------------ accepting

    /// <summary>The suggestion is accepted only if it still holds: an open item of the right kind that the amount does not exceed.</summary>
    [OnValidate(BankTransaction)]
    public async Task<ValidationResult> ValidateTransaction(Record tx, Erp.Sdk.AppContext ctx)
    {
        if (tx.Get<string>("status") != "matched" || tx.Get<Guid?>("openItem") is not null)
        {
            return ValidationResult.Ok;
        }

        if (tx.Get<Guid?>("suggestedOpenItem") is not { } itemId || await ctx.Data.GetAsync(OpenItem, itemId) is not { } item)
        {
            return ValidationResult.Error("suggestedOpenItem", "Velg hvilken åpen post transaksjonen gjelder før du godkjenner.");
        }

        var amount = tx.Get<decimal>("amount");
        var remaining = item.Get<decimal>("remaining");
        var failures = new List<ValidationFailure>();
        if (item.Get<string>("status") != "open" || Math.Sign(remaining) != Math.Sign(amount))
        {
            failures.Add(new ValidationFailure("suggestedOpenItem", "Posten er ikke åpen, eller er av feil type for denne transaksjonen."));
        }
        else if (Math.Abs(amount) > Math.Abs(remaining))
        {
            failures.Add(new ValidationFailure("suggestedOpenItem", $"Beløpet {Math.Abs(amount):0.00} er større enn åpent beløp {Math.Abs(remaining):0.00}."));
        }

        var bank = await ctx.Data.GetAsync(BankAccount, tx.Get<Guid>("bankAccount"));
        if (bank?.Get<Guid?>("ledgerAccount") is null)
        {
            failures.Add(new ValidationFailure("bankAccount", "Bankkontoen mangler hovedbokskonto."));
        }

        return failures.Count == 0 ? ValidationResult.Ok : ValidationResult.Errors(failures);
    }

    /// <summary>
    /// Accepting posts the receipt (or payment) against the bank account and settles the open item with it, in one transaction.
    /// A smaller amount than the item settles part of it.
    /// </summary>
    [OnAfterSave(BankTransaction)]
    public async Task AfterTransactionSaved(Record tx, Erp.Sdk.AppContext ctx)
    {
        if (tx.Get<string>("status") != "matched" || tx.Get<Guid?>("openItem") is not null)
        {
            return;
        }

        var item = (await ctx.Data.GetAsync(OpenItem, tx.Get<Guid>("suggestedOpenItem")))!;
        var bank = (await ctx.Data.GetAsync(BankAccount, tx.Get<Guid>("bankAccount")))!;
        var ledger = bank.Get<Guid>("ledgerAccount");
        var signed = tx.Get<decimal>("amount");
        var amount = Math.Abs(signed);
        var incoming = signed > 0;
        var date = tx.Get<DateOnly>("bookingDate");
        var party = item.Get<Guid>("party");
        var text = Truncate($"Bankavstemming {tx.Get<string>("counterpartyName")} {tx.Get<string>("message")}".Trim(), 500);

        var voucher = await ctx.Data.CreateAsync(Voucher, new Dictionary<string, object?> { ["voucherDate"] = date, ["description"] = text });
        await ctx.Data.CreateAsync(Line, new Dictionary<string, object?>
        {
            ["voucher"] = voucher.Id, ["account"] = ledger, [incoming ? "debit" : "credit"] = amount, ["description"] = text
        });
        await ctx.Data.CreateAsync(Line, new Dictionary<string, object?>
        {
            ["voucher"] = voucher.Id,
            ["account"] = item.Get<Guid>("account"),
            [incoming ? "credit" : "debit"] = amount,
            ["party"] = party,
            ["dueDate"] = date,
            ["description"] = text
        });
        await ctx.Data.TransitionAsync(Voucher, voucher.Id, "post");

        var entry = (await ctx.Data.QueryAsync(Entry, $"voucher eq '{voucher.Id}'", top: 1)).Single();
        var posting = (await ctx.Data.QueryAsync(Posting, $"entry eq '{entry.Id}'", top: 10))
            .Single(p => p.Get<Guid>("account") == item.Get<Guid>("account") && p.Get<Guid?>("party") == party);
        var counterItem = (await ctx.Data.QueryAsync(OpenItem, $"posting eq '{posting.Id}'", top: 1)).Single();

        var settlement = await ctx.Data.CreateAsync(Settlement, new Dictionary<string, object?> { ["settlementDate"] = date, ["description"] = text });
        await ctx.Data.CreateAsync(SettlementItem, new Dictionary<string, object?>
        {
            ["settlement"] = settlement.Id, ["openItem"] = item.Id, ["amount"] = incoming ? amount : -amount
        });
        await ctx.Data.CreateAsync(SettlementItem, new Dictionary<string, object?>
        {
            ["settlement"] = settlement.Id, ["openItem"] = counterItem.Id, ["amount"] = incoming ? -amount : amount
        });
        await ctx.Data.TransitionAsync(Settlement, settlement.Id, "complete");

        var current = (await ctx.Data.GetAsync(BankTransaction, tx.Id))!;
        await ctx.Data.UpdateAsync(BankTransaction, current.Id, current.RowVersion, new Dictionary<string, object?>
        {
            ["openItem"] = item.Id,
            ["voucher"] = voucher.Id,
            ["settlement"] = settlement.Id,
            ["note"] = amount == Math.Abs(item.Get<decimal>("remaining")) ? "Utlignet." : $"Delvis utlignet ({amount:0.00} av {Math.Abs(item.Get<decimal>("remaining")):0.00})."
        });
    }

    // ------------------------------------------------------------------ reconciliation against the ledger

    /// <summary>
    /// A reconciliation is a snapshot at a date: the bank's balance on the last statement up to it against the ledger balance of
    /// the bank account, and what is still waiting for review. The figures are set when the record is created.
    /// </summary>
    [OnAfterSave(BankReconciliation)]
    public async Task AfterReconciliationSaved(Record recon, Erp.Sdk.AppContext ctx)
    {
        if (recon.Get<decimal?>("ledgerBalance") is not null)
        {
            return;
        }

        var bank = await ctx.Data.GetAsync(BankAccount, recon.Get<Guid>("bankAccount"));
        if (bank is null)
        {
            return;
        }

        var asOf = recon.Get<DateOnly>("asOf");
        var statement = (await ctx.Data.QueryAsync(BankStatement, $"bankAccount eq '{bank.Id}' and toDate le '{asOf:yyyy-MM-dd}'", orderBy: "created_at", top: 5000))
            .Where(s => s.Get<DateOnly?>("toDate") is not null)
            .OrderBy(s => s.Get<DateOnly>("toDate")).LastOrDefault();

        decimal ledger = 0;
        var filter = $"account eq '{bank.Get<Guid>("ledgerAccount")}' and postingDate le '{asOf:yyyy-MM-dd}'";
        for (var skip = 0; ; skip += 1000)
        {
            var page = await ctx.Data.QueryAsync(Posting, filter, orderBy: "created_at", top: 1000, skip: skip);
            ledger += page.Sum(p => p.Get<decimal>("debit") - p.Get<decimal>("credit"));
            if (page.Count < 1000)
            {
                break;
            }
        }

        var waiting = (await ctx.Data.QueryAsync(BankTransaction, $"bankAccount eq '{bank.Id}' and bookingDate le '{asOf:yyyy-MM-dd}'", top: 5000))
            .Where(t => t.Get<string>("status") is "new" or "review").ToList();
        var bankBalance = statement?.Get<decimal?>("closingBalance") ?? 0m;

        var current = (await ctx.Data.GetAsync(BankReconciliation, recon.Id))!;
        await ctx.Data.UpdateAsync(BankReconciliation, current.Id, current.RowVersion, new Dictionary<string, object?>
        {
            ["bankBalance"] = bankBalance,
            ["statementDate"] = statement?.Get<DateOnly?>("toDate"),
            ["ledgerBalance"] = ledger,
            ["difference"] = bankBalance - ledger,
            ["unmatchedCount"] = waiting.Count,
            ["unmatchedAmount"] = waiting.Sum(t => t.Get<decimal>("amount"))
        });
    }
}
