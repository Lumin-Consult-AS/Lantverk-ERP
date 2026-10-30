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

    [OnEvent("core.party.deleted@1")]
    public async Task OnPartyDeleted(PartyDeleted e, Erp.Sdk.AppContext ctx)
        => await ctx.Data.DeleteWhere("demo.contactnote", f => f["party"] == e.PartyId);
}

