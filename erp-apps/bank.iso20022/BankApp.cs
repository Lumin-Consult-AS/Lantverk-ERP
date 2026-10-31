using Erp.Sdk;

namespace Bank.Iso20022;

public sealed record BatchReleased(Guid BatchId);

public sealed record ImportRequested(Guid ImportId);

/// <summary>
/// Bank adapter that works with ISO 20022 files (ADR-0028). It fulfils the bank contract of <c>finance</c>: a released payment
/// batch becomes a pain.001 file attached to the batch; uploaded camt.053 / camt.054 files become bank statements and bank
/// transactions. It has no connection to a bank; a person (or a later adapter) moves the files.
/// </summary>
public sealed class BankApp : AppBase
{
    private const string Batch = "finance.paymentbatch";
    private const string Payment = "finance.payment";
    private const string BankAccount = "finance.bankaccount";
    private const string Statement = "finance.bankstatement";
    private const string Transaction = "finance.banktransaction";
    private const string Import = "bank.import";

    // ------------------------------------------------------------------ sending payments

    /// <summary>
    /// Makes the payment file for a released batch, checks it against the ISO 20022 schema, stores it and records the outcome on
    /// the batch. A file that fails the check is never handed out. Safe to deliver again: a batch that is answered is left alone.
    /// </summary>
    [OnEvent("finance.paymentbatch.released@1")]
    public async Task OnBatchReleased(BatchReleased e, Erp.Sdk.AppContext ctx)
    {
        var batch = await ctx.Data.GetAsync(Batch, e.BatchId);
        if (batch is null || batch.Get<string>("status") != "released" || batch.Get<string>("deliveryStatus") is not (null or "none"))
        {
            return;
        }

        var result = new Dictionary<string, object?>();
        try
        {
            var bank = await ctx.Data.GetAsync(BankAccount, batch.Get<Guid>("bankAccount"))
                ?? throw new BankFileException("Bankkontoen finnes ikke.");
            var payments = await ctx.Data.QueryAsync(Payment, $"batch eq '{batch.Id}'", orderBy: "created_at", top: 5000);
            var file = new PaymentFile(
                batch.Id.ToString("N"),
                DateTime.UtcNow,
                batch.Get<DateOnly>("executionDate"),
                bank.Get<string>("holderName")!,
                bank.Get<string>("holderId"),
                bank.Get<string>("accountNumber")!,
                string.IsNullOrWhiteSpace(bank.Get<string>("bic")) ? null : bank.Get<string>("bic"),
                bank.Get<string>("currency")!,
                payments.Select(p => new PaymentInstruction(
                    p.Id.ToString("N"),
                    p.Get<string>("creditorName")!,
                    p.Get<string>("creditorAccount")!,
                    p.Get<decimal>("amount"),
                    p.Get<string>("currency")!,
                    string.IsNullOrWhiteSpace(p.Get<string>("paymentReference")) ? null : p.Get<string>("paymentReference"),
                    string.IsNullOrWhiteSpace(p.Get<string>("message")) ? null : p.Get<string>("message"))).ToList());

            var xml = Pain001.Write(file);
            var errors = Pain001.Validate(xml);
            if (errors.Count > 0)
            {
                throw new BankFileException($"Betalingsfilen følger ikke ISO 20022-skjemaet (pain.001.001.03): {string.Join(" | ", errors.Take(3))}");
            }

            var fileName = $"pain.001_{batch.Get<string>("name")?.Replace(' ', '_')}_{file.ExecutionDate:yyyyMMdd}.xml";
            result["file"] = await ctx.Documents.StoreDocumentAsync(fileName, "application/xml", xml);
            result["deliveryStatus"] = "sent";
            result["message"] = $"Betalingsfil (pain.001.001.03) med {payments.Count} betalinger er laget. Last den ned og lever den til banken.";
        }
        catch (Exception ex) when (ex is BankFileException or ArgumentException or InvalidOperationException or Grpc.Core.RpcException)
        {
            result["deliveryStatus"] = "failed";
            result["message"] = $"Betalingsfilen kunne ikke lages: {ex.Message}";
        }

        var current = await ctx.Data.GetAsync(Batch, e.BatchId) ?? batch;
        await ctx.Data.UpdateAsync(Batch, current.Id, current.RowVersion, result);
    }

    // ------------------------------------------------------------------ reading statements and notifications

    [OnValidate(Import)]
    public async Task<ValidationResult> ValidateImport(Record import, Erp.Sdk.AppContext ctx)
    {
        if (import.Get<Guid?>("file") is { } fileId && await ctx.Documents.GetDocumentAsync(fileId) is null)
        {
            return ValidationResult.Error("file", "Filen finnes ikke. Last den opp først.");
        }

        return ValidationResult.Ok;
    }

    [OnAfterSave(Import)]
    public async Task AfterImportSaved(Record import, Erp.Sdk.AppContext ctx)
    {
        if (import.Get<string>("status") == "requested")
        {
            await ctx.Events.PublishAsync("bank.import.requested@1", import.Id.ToString(), new { importId = import.Id });
        }
    }

    /// <summary>
    /// Reads the uploaded file and creates the bank statements and bank transactions it holds that are not there already (so
    /// reading the same file twice adds nothing). The file must belong to the chosen bank account.
    /// </summary>
    [OnEvent("bank.import.requested@1")]
    public async Task OnImportRequested(ImportRequested e, Erp.Sdk.AppContext ctx)
    {
        var import = await ctx.Data.GetAsync(Import, e.ImportId);
        if (import is null || import.Get<string>("status") != "requested")
        {
            return;
        }

        var result = new Dictionary<string, object?>();
        try
        {
            var bank = await ctx.Data.GetAsync(BankAccount, import.Get<Guid>("bankAccount"))
                ?? throw new BankFileException("Bankkontoen finnes ikke.");
            var document = await ctx.Documents.GetDocumentAsync(import.Get<Guid>("file"))
                ?? throw new BankFileException("Filen finnes ikke.");
            var statements = CamtReader.Read(document.Content, out var isStatement);

            var accountNumber = Pain001.NormalizeAccount(bank.Get<string>("accountNumber")!);
            var existingStatements = (await ctx.Data.QueryAsync(Statement, "externalId ne ''", top: 5000))
                .ToDictionary(s => s.Get<string>("externalId")!, s => s.Id);
            var existingTransactions = (await ctx.Data.QueryAsync(Transaction, "externalId ne ''", top: 20000))
                .Select(t => t.Get<string>("externalId")!).ToHashSet();

            int newStatements = 0, newTransactions = 0, duplicates = 0;
            foreach (var statement in statements)
            {
                if (statement.AccountId != accountNumber)
                {
                    throw new BankFileException($"Filen gjelder konto {statement.AccountId}, ikke {accountNumber}.");
                }

                Guid? statementId = null;
                if (isStatement && statement.StatementId is { } externalId)
                {
                    var key = $"{bank.Id:N}:{externalId}";
                    if (existingStatements.TryGetValue(key, out var found))
                    {
                        statementId = found;
                    }
                    else
                    {
                        statementId = (await ctx.Data.CreateAsync(Statement, new Dictionary<string, object?>
                        {
                            ["bankAccount"] = bank.Id,
                            ["externalId"] = key,
                            ["fromDate"] = statement.From,
                            ["toDate"] = statement.To,
                            ["openingBalance"] = statement.OpeningBalance,
                            ["closingBalance"] = statement.ClosingBalance,
                            ["currency"] = statement.Currency
                        })).Id;
                        newStatements++;
                    }
                }

                foreach (var entry in statement.Entries)
                {
                    var key = $"{bank.Id:N}:{entry.Reference}";
                    if (!existingTransactions.Add(key))
                    {
                        duplicates++;
                        continue;
                    }

                    await ctx.Data.CreateAsync(Transaction, new Dictionary<string, object?>
                    {
                        ["bankAccount"] = bank.Id,
                        ["statement"] = statementId,
                        ["externalId"] = key,
                        ["bookingDate"] = entry.BookingDate,
                        ["valueDate"] = entry.ValueDate,
                        ["amount"] = entry.Amount,
                        ["currency"] = entry.Currency,
                        ["counterpartyName"] = entry.CounterpartyName,
                        ["counterpartyAccount"] = entry.CounterpartyAccount,
                        ["paymentReference"] = entry.PaymentReference,
                        ["message"] = entry.Message,
                        ["endToEndId"] = entry.EndToEndId
                    });
                    newTransactions++;
                }
            }

            result["statementCount"] = newStatements;
            result["transactionCount"] = newTransactions;
            result["duplicateCount"] = duplicates;
            result["status"] = "done";
            result["message"] = $"{(isStatement ? "Kontoutskrift (camt.053)" : "Innbetalingsmelding (camt.054)")} er lest: {newTransactions} nye transaksjoner, {duplicates} var lest før.";
        }
        catch (Exception ex) when (ex is BankFileException or FormatException or ArgumentException or InvalidOperationException or Grpc.Core.RpcException)
        {
            result["status"] = "failed";
            result["message"] = ex.Message;
        }

        var current = await ctx.Data.GetAsync(Import, e.ImportId) ?? import;
        await ctx.Data.UpdateAsync(Import, current.Id, current.RowVersion, result);
    }
}
