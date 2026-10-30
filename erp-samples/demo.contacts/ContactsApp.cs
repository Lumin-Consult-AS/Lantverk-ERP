using Erp.Sdk;

namespace Demo.Contacts;

public sealed record PartyDeleted(Guid PartyId);

public sealed class ContactsApp : AppBase
{
    [OnValidate("demo.contactnote")]
    public Task<ValidationResult> ValidateNote(Record note, Erp.Sdk.AppContext ctx)
        => Task.FromResult(note.Get<DateOnly?>("followUp") < ctx.Today
            ? ValidationResult.Error("followUp", "Oppfølgingsdato kan ikke være tilbake i tid")
            : ValidationResult.Ok);

    /// <summary>
    /// 1.0.0 -> 1.1.0: contact notes got a "priority"; existing notes become "normal". Works in batches through the data
    /// API and is safe to run again: it only touches notes without a priority, so after a failure the core can simply call
    /// it once more and it carries on where the data stands.
    /// </summary>
    [Migration("Migrations.V110")]
    public async Task<MigrationStep> MigrateToV110(Erp.Sdk.AppContext ctx, MigrationState state)
    {
        const int batchSize = 100;
        var notes = await ctx.Data.QueryAsync("demo.contactnote", "priority isempty", top: batchSize);
        foreach (var note in notes)
        {
            await ctx.Data.UpdateAsync("demo.contactnote", note.Id, note.RowVersion, new Dictionary<string, object?> { ["priority"] = "normal" });
        }

        // The checkpoint only counts batches (the query itself finds what is left), which also makes progress visible.
        var batch = int.TryParse(state.Checkpoint, out var done) ? done + 1 : 1;
        return notes.Count < batchSize
            ? MigrationStep.Done(notes.Count)
            : MigrationStep.Continue(batch.ToString(), notes.Count);
    }

    [OnEvent("core.party.deleted@1")]
    public async Task OnPartyDeleted(PartyDeleted e, Erp.Sdk.AppContext ctx)
        => await ctx.Data.DeleteWhere("demo.contactnote", f => f["party"] == e.PartyId);
}

