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

    /// <summary>
    /// Offline command: set the priority of a note. The rule for conflicts is "adjust": when the note changed on the server
    /// while the device was offline, a higher priority set meanwhile is kept and the device is told so.
    /// </summary>
    [Command("demo.contacts.setpriority")]
    public async Task<CommandResult> SetPriority(CommandContext cmd, Erp.Sdk.AppContext ctx)
    {
        var target = cmd.Targets.Single();
        var requested = cmd.Arg<string>("priority") ?? "normal";
        var current = target.Exists ? await ctx.Data.GetAsync(target.ObjectId, target.RecordId) : null;
        if (current is null)
        {
            return CommandResult.Rejected("Notatet finnes ikke lenger.");
        }

        var serverPriority = current.Get<string>("priority");
        if (cmd.HasConflict && serverPriority == "high" && requested != "high")
        {
            return CommandResult.Adjusted("Prioriteten ble satt til «high» på serveren mens enheten var frakoblet, og den ble beholdt.");
        }

        await ctx.Data.UpdateAsync(
            target.ObjectId, target.RecordId, current.RowVersion, new Dictionary<string, object?> { ["priority"] = requested });
        return cmd.HasConflict
            ? CommandResult.Adjusted("Notatet var endret på serveren; prioriteten ble satt likevel.")
            : CommandResult.Applied();
    }

    /// <summary>Offline command: set the follow-up date. A changed note is rejected; the normal validation (no past dates) applies.</summary>
    [Command("demo.contacts.setfollowup")]
    public async Task<CommandResult> SetFollowUp(CommandContext cmd, Erp.Sdk.AppContext ctx)
    {
        var target = cmd.Targets.Single();
        await ctx.Data.UpdateAsync(
            target.ObjectId,
            target.RecordId,
            target.CurrentRowVersion,
            new Dictionary<string, object?> { ["followUp"] = cmd.Arg<string>("followUp") });
        return CommandResult.Applied();
    }

    /// <summary>Offline command: delete a note. If it changed meanwhile a person decides (manual queue).</summary>
    [Command("demo.contacts.deletenote")]
    public async Task<CommandResult> DeleteNote(CommandContext cmd, Erp.Sdk.AppContext ctx)
    {
        var target = cmd.Targets.Single();
        if (!target.Exists)
        {
            return CommandResult.Applied("Notatet var allerede slettet.");
        }

        await ctx.Data.DeleteAsync(target.ObjectId, target.RecordId, target.CurrentRowVersion);
        return CommandResult.Applied();
    }

    [OnEvent("core.party.deleted@1")]
    public async Task OnPartyDeleted(PartyDeleted e, Erp.Sdk.AppContext ctx)
        => await ctx.Data.DeleteWhere("demo.contactnote", f => f["party"] == e.PartyId);
}

