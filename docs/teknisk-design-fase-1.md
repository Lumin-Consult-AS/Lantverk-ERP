# ERP-kjerne – teknisk design fase 1

Sist oppdatert 30. oktober 2026 · Jens Nymoen

## Formål og forutsetninger

Dette dokumentet er det tekniske designet for fase 1 i `docs/plan.md`: kjernen, app-rammeverket, flyt- og regelmotoren, dokumentmottaket, frakoblet synk og den generiske UI-klienten. Det er grunnlaget for å begynne å kode, og alle valg her er forslag som kan endres før de er implementert. Stegvis gjennomføring står i `docs/IMPLEMENTATION_PLAN.md`.

**Beslutninger fra planen som designet bygger på**

| Beslutning | Konsekvens her |
| --- | --- |
| C#/.NET, PostgreSQL, Linux | Nyeste .NET LTS, EF Core mot PostgreSQL, containere på Kubernetes |
| Kun multi-tenant | Én database, tenant-ID på alle rader, rad-nivå-sikkerhet (RLS) i PostgreSQL |
| Apper som egne prosesser | Hver app er en container som snakker med kjernen over gRPC; kjernen kaller aldri app-kode i egen prosess |
| Deklarativt UI | Apper leverer sidedefinisjoner som data; én generisk klient rendrer dem |
| Land som grunnbegrep | Land, valuta og selskap er kjerneobjekter fra første migrasjon |
| Lukket kjerne, åpent SDK | SDK, manifestformat, proto-kontrakter og eksempelapper ligger i egne, offentlige repoer |
| 20 timer i uken, én person | Designet velger det enkleste som oppfyller kravet; alt som kan utsettes er markert |

**Fase 1 er ferdig når** en demo-app («Kontakter») kan installeres i en tenant, legge et felt på et objekt eid av en annen app, publisere og abonnere på en hendelse, vise en liste- og kortside i klienten, og avinstalleres – uten kodeendring i kjernen. I tillegg skal en flyt med to godkjennere kunne defineres og kjøres, et dokument kunne mottas i innboksen, og endringer gjort frakoblet kunne synkes med konfliktløsning.

## Kjøretidsarkitektur

Kjernen er én .NET-tjeneste (kan deles opp senere) som eier databasen. Hver app er en egen container som kjernen starter, overvåker og kaller over gRPC. Apper når aldri databasen direkte; alt går gjennom kjernens data-API, som håndhever tenant, tillatelser og metadata.

*Oversikt: generisk klient (nettleser, mobil) ⇄ REST ⇄ kjerne (API og auth, metadata, hendelsesbuss, flyt og regler, dokumenter, app-vert) ⇄ gRPC ⇄ app-containere (salg, regnskap, partnerapper). Kjernen skriver til PostgreSQL (tenant-ID og RLS på alle rader) og objektlager. App-containere har ingen direkte databasetilgang.*

**Prosesser og ansvar**

| Komponent | Kjører som | Ansvar |
| --- | --- | --- |
| Kjerne-API | ASP.NET Core, horisontalt skalerbar | REST for klient og integrasjoner, autentisering, tenant-oppløsning, data-API mot metadata |
| App-vert | Del av kjernen, med Kubernetes-API-tilgang | Starter app-containere per app-versjon (ikke per tenant), ruter kall til riktig versjon, helsesjekk, ressursgrenser |
| App-container | Én container per app og versjon, delt av alle tenanter | Kjører appens forretningslogikk; stateless; får tenant-kontekst i hvert kall |
| Bakgrunnsarbeider | Egen kjerneprosess | Leverer hendelser fra outbox, kjører planlagte jobber, flyt-tidsfrister |
| PostgreSQL | Managed eller egen, EU-region | All strukturert data, outbox, flyt-tilstand |
| Objektlager | S3-kompatibelt (f.eks. Hetzner Object Storage, Azure Blob EU) | Dokumenter, vedlegg, eksporter |

**Tenant-isolasjon**

- Hver forespørsel bærer tenant-ID fra tokenet; kjernen setter `SET app.tenant_id` på databaseforbindelsen, og RLS-policyer filtrerer alle tabeller på den.
- App-containere er delt mellom tenanter (én container per app-versjon). Det er billigere og enklere, men betyr at en app aldri får holde tilstand i minnet mellom kall. Per-tenant-containere kan innføres senere uten å endre app-kontrakten.
- Kall fra kjerne til app har tidsfrist (5 s synkront, lengre for bakgrunnsjobber) og ressursgrenser i Kubernetes, slik at en dårlig app ikke tar ned andre.

**Bevisst utsatt:** WebAssembly-sandkasse, per-tenant-containere, oppdeling av kjernen i mikrotjenester, multi-region.

## Metadatamodell

Metadata er kjernens viktigste del: alt en app eier eller utvider registreres her, og data-API, UI, tillatelser og rapportering leser fra den. Modellen er bevisst liten.

| Begrep | Hva det er | Eksempel |
| --- | --- | --- |
| Objekt | En navngitt entitet med felt, eid av én app. Stabil ID `app.objekt` | `core.party`, `sales.order`, `sales.orderline` |
| Felt | Navn, type, påkrevd, standardverdi, validering. Eid av appen som la det til – som kan være en annen enn objektets eier | `sales.order.deliverydate` (eid av salg), `sales.order.shopifyid` (eid av Shopify-appen) |
| Relasjon | Felt av type referanse til et annet objekt, med slette-regel | `sales.order.customer → core.party` |
| Nøkkel | Primærnøkkel er alltid en UUID generert av kjernen; apper kan definere unike forretningsnøkler i tillegg | `sales.order.number` unik per selskap |
| Enum | Navngitt verdiliste som apper kan utvide med nye verdier | `core.documentstatus`: draft, approved, posted, archived |
| Grensesnitt | Et sett operasjoner en app tilbyr og andre implementerer | `sales.poschannel` med `PushPrices`, `PullSales` |
| Hendelse | Navngitt, versjonert nyttelast en app publiserer | `sales.order.posted` v1 |

**Felttyper i fase 1:** string, text, integer, decimal (med skala), boolean, date, datetime, uuid, reference, enum, json, money (beløp + valuta), blob-referanse.

**Utvidelse av andres objekter**

- En app kan legge felt på et hvilket som helst objekt som ikke er merket `sealed` av eieren. Feltet lagres i samme tabell (egen kolonne), ikke i en JSON-pose, slik at det kan indekseres og spørres som alle andre felt.
- Feltnavn prefikses implisitt med eierappens navn i metadata, så to apper kan legge til «rabatt» uten kollisjon.
- Eieren av objektet kan ikke slette eller endre type på felt eid av andre, og omvendt. Avinstallasjon fjerner appens felt fra metadata, men kolonnen beholdes (merket foreldreløs) i en karensperiode før den droppes.

**Versjonering**

- Metadata lagres med appversjonen som registrerte dem. Nye felt, enum-verdier eller hendelsesfelt er bakoverkompatible (minor). Fjerning eller typeendring er brytende (major) og krever migrasjonsskript.
- Kjernen tar et øyeblikksbilde av tenantens samlede metadata ved hver installasjon/oppgradering, slik at «hva så skjemaet ut som da» kan besvares.

**Bevisst utsatt:** beregnede felt, objektarv, flerspråklige feltnavn ut over nb/en, tillatelser på feltnivå (fase 2).

## Lagring i PostgreSQL

Én database, ett skjema per app (`core`, `sales`, `finance`, …) og én tabell per objekt. Felt fra andre apper legges som kolonner i eierens tabell. Det gir vanlige SQL-spørringer, indekser og constraints, i motsetning til EAV- eller JSON-baserte metadatalagre.

**Obligatoriske kolonner på alle objekttabeller**

```sql
id            uuid primary key,
tenant_id     uuid not null,
company_id    uuid null,          -- null for tenant-globale objekter
created_at    timestamptz not null,
created_by    uuid not null,
updated_at    timestamptz not null,
updated_by    uuid not null,
row_version   bigint not null     -- optimistisk låsing
```

**Rad-nivå-sikkerhet**

```sql
alter table sales."order" enable row level security;
create policy tenant_isolation on sales."order"
  using (tenant_id = current_setting('app.tenant_id')::uuid);
```

Kjernen kobler til med en databaserolle uten `BYPASSRLS`, og setter `app.tenant_id` ved starten av hver transaksjon. En glemt `where tenant_id = …` kan dermed ikke lekke data mellom tenanter. Migrasjons- og driftsjobber bruker en egen rolle.

**Skjemamigrering**

- Kjernen genererer DDL fra metadata ved installasjon og oppgradering; apper skriver aldri SQL selv. DDL kjøres uten lange låser (`add column … null` uten default, `create index concurrently`).
- Datamigrasjoner i en app skrives i C# mot data-API-et og kjøres av kjernen per tenant med fremdriftslogg og gjenopptak.
- Kjernens egne tabeller migreres med EF Core-migrasjoner.

**Revisjonsspor**

- Hver objekttabell får en skyggetabell `<tabell>_audit` med gammel og ny rad som JSONB, bruker, tidspunkt og årsak, fylt av en database-trigger slik at det ikke kan omgås.
- Audit-tabellene er append-only (ingen `update`/`delete` for applikasjonsrollen).

**Bevisst utsatt:** partisjonering per tenant, lesereplikaer, arkivering av gamle audit-rader.

## App-manifest

Manifestet er en YAML-fil (`app.yaml`) i roten av app-pakken, sammen med sidedefinisjoner, tillatelsessett og eventuell kode. Kjernen validerer det mot et publisert JSON-skjema før installasjon. Eksempelet er demo-appen fra portkriteriet.

```yaml
id: demo.contacts
version: 1.0.0
name: Kontakter
publisher: Jens Nymoen
license: proprietary
sdk: ">=1.0 <2.0"

dependencies:
  core.parties: ">=1.0 <2.0"

runtime:
  image: ghcr.io/example/demo-contacts:1.0.0   # utelates for rent deklarative apper
  resources: { cpu: 250m, memory: 256Mi }
  egress: []                                    # ingen eksterne URL-er

objects:
  demo.contactnote:
    label: Kontaktnotat
    fields:
      party:    { type: reference, to: core.party, required: true, onDelete: cascade }
      text:     { type: text, required: true }
      followUp: { type: date }

extensions:
  core.party:
    fields:
      preferredChannel: { type: enum, of: demo.channel }

enums:
  demo.channel: [email, phone, sms]

events:
  publishes:
    - demo.contactnote.created@1
  subscribes:
    - core.party.deleted@1: OnPartyDeleted

pages:
  - pages/contactnote-list.yaml
  - pages/contactnote-card.yaml
  - extend: core.party-card   # legger en fane på kjernens partkort
    file: pages/party-card-ext.yaml

permissionSets:
  - id: demo.contacts.user
    label: Kontaktbruker
    grants: { demo.contactnote: [read, create, update] }

migrations:
  - from: 0.9.0
    to: 1.0.0
    run: Migrations.V100
```

**Regler kjernen håndhever ved installasjon**

1. Alle `dependencies` må være installert i tenanten i en versjon innenfor intervallet.
2. Objekter og felt må bruke appens eget prefiks; utvidelser kan bare peke på objekter som ikke er `sealed`.
3. `egress` er en hvit liste; app-containeren får nettverkspolicy som kun tillater disse (pluss kjernen).
4. Hendelser appen abonnerer på må finnes i en installert app.
5. En app uten `runtime` er rent deklarativ og trenger ingen container – kjernen håndterer CRUD, sider og enkle valideringer selv. Dette er den billigste typen app og bør dekke mange partnerbehov.

**Pakkeformat:** en zip signert av utgiver, med `app.yaml`, `pages/`, `permissions/`, `i18n/` og en referanse til container-image. Kjernen verifiserer signaturen og lagrer pakken i objektlageret.

## App-SDK og kontrakter

SDK-et er en NuGet-pakke (`Erp.Sdk`) med en gRPC-server kjernen kaller inn i, en klient for kjernens data-API, og typede hjelpere generert fra appens manifest. Kontraktene er definert i Protobuf og publisert åpent.

| Retning | Kontrakt | Brukes til |
| --- | --- | --- |
| Kjerne → app | `AppService` (gRPC-server i appen) | Hendelseslyttere, valideringshooks, grensesnitt-implementasjoner, migrasjoner, helsesjekk |
| App → kjerne | `DataService`, `EventService`, `WorkflowService`, `DocumentService` (gRPC-klienter i SDK-et) | Lese og skrive data, publisere hendelser, starte flyter, hente dokumenter |

Hvert kall fra kjernen bærer kontekst: tenant, selskap, bruker, korrelasjons-ID og et kortlevd token som appen må sende tilbake i alle kall til kjernen. Appen kan dermed aldri gjøre mer enn brukeren som utløste kallet.

**Hooks i fase 1**

```csharp
public class ContactsApp : AppBase
{
    [OnValidate("demo.contactnote")]
    public Task<ValidationResult> ValidateNote(Record note, AppContext ctx)
        => Task.FromResult(note.Get<DateOnly?>("followUp") < ctx.Today
            ? ValidationResult.Error("followUp", "Oppfølgingsdato kan ikke være tilbake i tid")
            : ValidationResult.Ok);

    [OnEvent("core.party.deleted@1")]
    public async Task OnPartyDeleted(PartyDeleted e, AppContext ctx)
        => await ctx.Data.DeleteWhere("demo.contactnote", f => f["party"] == e.PartyId);
}
```

- `OnValidate` kjøres synkront før lagring og kan avvise. `OnBeforeSave`/`OnAfterSave` kan endre feltverdier. Alle tre har 5 sekunders tidsfrist.
- `OnEvent` kjøres asynkront fra hendelsesbussen, minst én gang; handleren må være idempotent.
- `Implements<T>` registrerer appen som leverandør av et grensesnitt definert av en annen app (f.eks. en POS-adapter mot `sales.poschannel`).

**Data-API-et** er generisk (`Get`, `Query`, `Create`, `Update`, `Delete`, `Batch`) over `Record`-objekter definert av metadata, med typede wrappere generert fra manifestet. Spørringer uttrykkes som et filter-tre (felt, operator, verdi, og/eller), ikke SQL. Alt en hook gjør gjennom data-API-et skjer i samme databasetransaksjon som den utløsende operasjonen.

**Lokal utvikling:** `erp dev` (CLI i SDK-et) starter en lokal kjerne i Docker med en test-tenant, installerer appen fra arbeidskatalogen med hot reload av manifest og sider, og eksponerer klienten på localhost.

**Bevisst utsatt:** SDK for andre språk (TypeScript er neste kandidat), batch-jobber med egen planlegging, rapportdatakilder.

## Identitet, tilgang og tillatelser

Kjernen er ikke selv en identitetsleverandør. Den bruker OpenID Connect og lar hver tenant koble til Entra ID, Google Workspace eller en innebygd brukerdatabase for små kunder. Forslag: Keycloak eller Zitadel som selvdriftet OIDC-leverandør, med federasjon mot Entra ID som første eksterne. Valget er åpent.

| Begrep | Innhold |
| --- | --- |
| Tenant | Kunden; eier alle data og alle brukere |
| Selskap | Juridisk enhet i tenanten; brukere kan ha tilgang til ett eller flere |
| Bruker | Knyttet til én tenant; identifisert med `sub` fra OIDC-leverandøren |
| Tjenestebruker | For integrasjoner og apper; klient-ID og hemmelighet eller mTLS, aldri passord |
| Tillatelsessett | Rettigheter på objekter (read/create/update/delete/post) og handlinger, definert av apper i manifestet |
| Rolle | Tenantens sammensetning av tillatelsessett, eventuelt begrenset til selskap |

**Håndheving**

- Tillatelser evalueres i kjernens data-API, ikke i UI og ikke i appene.
- Objektnivå i fase 1; feltnivå kommer i fase 2.
- Enkle radfilter («kan bare se ordrer for egen avdeling») uttrykkes som filter knyttet til rollen og legges automatisk på alle spørringer.
- Apper som kjører bakgrunnsjobber bruker en tjenestebruker tenanten har tildelt roller; aldri en «systemrolle» som ser alt.

**Token-flyt:** klienten får et JWT fra OIDC-leverandøren; kjernen validerer det og bytter til et internt, kortlevd token (tenant, bruker, roller, selskap) som sendes videre til apper over gRPC. Apper får aldri det eksterne tokenet.

## Hendelsesbuss

Fase 1 bruker transaksjonell outbox i PostgreSQL med en bakgrunnsarbeider som leverer. Det gir den viktigste garantien: en hendelse publiseres hvis og bare hvis transaksjonen som utløste den ble bekreftet. NATS eller Kafka kan settes inn bak samme grensesnitt senere.

**Hendelser**

- Navn og versjon: `sales.order.posted@1`. Nyttelasten er JSON definert i publisererens manifest; nye felt beholder versjonen, fjerning eller endring gir ny versjon, og begge kan publiseres parallelt i en overgangsperiode.
- Alle hendelser bærer tenant, selskap, utløsende bruker, korrelasjons-ID og tidspunkt.
- Hendelser lagres i `core.event_log` per tenant i minst 90 dager (åpent valg), og kan spilles av på nytt til en app.

**Levering**

- Hver abonnent (app + handler) har sin egen leveringskø med forsøk, eksponentiell backoff og dødbrevkø. En app som feiler stopper ikke andre.
- Levering er «minst én gang» i rekkefølge per aggregat-ID, ikke globalt. Handlere må være idempotente; SDK-et har en hjelper for å registrere behandlede hendelses-ID-er.
- Synkrone hooks er ikke hendelser; de går direkte kjerne → app med tidsfrist. Hendelser kan aldri avvise en transaksjon.

**Webhooks utad** er samme mekanisme med HTTP-levering, signert med tenantens hemmelighet.

**Bevisst utsatt:** eksakt-én-gang-semantikk, globale rekkefølgegarantier, lagring ut over 90 dager.

## Flyt- og regelmotor

Motoren har to deler: en **statusmaskin** per objekt og en **flyt** som kan knyttes til en overgang (hvem må godkjenne før overgangen skjer). Begge er deklarative. Apper definerer standardene; tenanten kan overstyre flytene uten utvikler.

**Statusmaskin** (i appens manifest)

```yaml
status:
  field: status
  initial: draft
  transitions:
    submit:  { from: draft,    to: pending,  permission: update, workflow: approval }
    approve: { from: pending,  to: approved, permission: approve }
    reject:  { from: pending,  to: draft,    permission: approve }
    post:    { from: approved, to: posted,   permission: post, locksRecord: true }
```

**Flytdefinisjon** (tenantens konfigurasjon, redigeres i UI, lagres som data)

```yaml
id: finance.vendorinvoice.approval
object: finance.vendorinvoice
trigger: submit
steps:
  - name: Avdelingsleder
    assignee: { rule: dimension-owner, dimension: department }
    when: { field: amount, gt: 0 }
  - name: Økonomisjef
    assignee: { role: finance.manager }
    when: { field: amount, gt: 50000 }
  - name: Daglig leder
    assignee: { user: <uuid> }
    when: { field: amount, gt: 250000 }
mode: sequential            # eller parallel
deadline: { days: 3, escalateTo: { role: finance.manager } }
delegation: allowed
onComplete: approve
onReject: reject
```

**Hva motoren gjør**

- Evaluerer `when`-reglene ved start og lager kun stegene som gjelder; en faktura på 20 000 får ett steg, en på 300 000 får tre.
- Oppretter en oppgave per steg i `core.task` med mottaker, frist og lenke. Oppgaver vises i klienten, kan sendes på e-post og besvares derfra via signert lenke.
- Låser objektet mens flyten pågår, unntatt felt merket `editableInWorkflow` (f.eks. kontering på et bilag).
- Logger hvert steg (hvem, når, kommentar, hva som ble endret) i revisjonssporet.
- Publiserer `core.workflow.started@1`, `core.workflow.stepcompleted@1`, `core.workflow.completed@1`.

**Regelmotor** i fase 1 er begrenset til `when`-uttrykk: felt, operator (eq, ne, gt, lt, in, isEmpty), verdi, og/eller. Beregninger og oppslag i andre objekter løses med hooks i apper.

**Bevisst utsatt:** parallelle grener med sammenføyning, flyter over flere objekter, visuell flytdesigner.

**Unntaksstyrt startside – utvidelsespunkt**

Startsiden viser det som krever handling nå. Den eies av kjernen, men innholdet kommer fra appene gjennom utvidelsespunktet `attention`, slik at hver ny app bidrar med sine unntak uten endring i kjernen. Oppgaver fra flytmotoren er første kilde.

```yaml
attention:
  - id: inventory.belowminimum
    label: Varer under minimum
    source: { query: inventory.item, filter: { available: { lt: "{minimum}" } } }
    severity: warning
    audience: { permission: inventory.item.read }
    action: { page: inventory.replenishment-list }
  - id: core.task.mine
    label: Til godkjenning
    source: { query: core.task, filter: { assignee: "{me}", status: open } }
    severity: action
```

- To kildetyper: **spørring** (deklarativ, kjernen teller og viser) og **hook** (`OnAttention` i appen, for unntak som krever beregning, f.eks. frister fra Skatteetaten eller uavstemte bankposter).
- Hvert unntak har alvorlighet (action, warning, info), målgruppe styrt av tillatelser, og en handling som åpner riktig side med filter.
- Kjernen cacher tellingene per bruker og oppdaterer dem på relevante hendelser.
- Brukeren kan skjule eller utsette et unntak.
- Regnskapsførerportalen er samme mekanisme samlet på tvers av flere tenanter.

## Dokumentmottak og dokumenttjeneste

Dokumenttjenesten eier alle filer og alle innkommende dokumenter. I fase 1 bygges mottak og lagring; AI-tolkning og EHF kommer i fase 2 som apper oppå.

| Objekt | Innhold |
| --- | --- |
| `core.file` | Binærfil i objektlageret: navn, type, størrelse, SHA-256, opplastet av, tenant/selskap. Uforanderlig; ny versjon = ny fil |
| `core.inbox` | Én innboks per selskap og kanal (e-postadresse, opplasting, API), med unik e-postadresse per innboks |
| `core.inboxitem` | Et mottatt dokument: kilde, tidspunkt, avsender, vedlegg, status (new, claimed, processed, rejected) |
| `core.attachment` | Kobling fil ↔ ethvert objekt, med kategori (bilag, avtale, bilde) |

**Flyt for et innkommende dokument**

1. E-post mottas av en innkommende e-posttjeneste (åpent valg) som kaller kjernens webhook.
2. Kjernen lagrer vedleggene som `core.file`, oppretter `core.inboxitem` og publiserer `core.inbox.received@1`.
3. En app som abonnerer (i fase 2: fakturatolken) tar elementet, behandler det og markerer det `processed` med lenke til objektet den laget. Elementer ingen tar vises som manuell arbeidsliste.

**Krav i fase 1**

- Virusskanning før tilgjengeliggjøring (ClamAV i container).
- Dedupering på SHA-256 per selskap, med varsel.
- Oppbevaringsregler per kategori: bilag kan ikke slettes i oppbevaringsperioden (5 år standard, konfigurerbart per land).
- PDF fra HTML-mal (Playwright/Chromium headless i egen container); utsending på e-post.

**Bevisst utsatt:** AI-tolkning, EHF/PEPPOL inn og ut, skanning fra mobil, OCR.

## Deklarativt UI

Apper leverer sidedefinisjoner som YAML; én generisk klient rendrer dem. Klienten vet hva en liste, et kort, en fane, et felt og en handling er – ikke hva salg eller regnskap er.

**Sidetyper i fase 1:** `list` (filter, sortering, søk, massehandlinger), `card` (grupper, faner, underlister), `dialog`, `worklist`.

```yaml
id: demo.contactnote-card
type: card
object: demo.contactnote
title: "{party.name} – {followUp}"
groups:
  - label: Generelt
    fields: [party, followUp]
  - label: Notat
    fields: [text]
actions:
  - id: markDone
    label: Marker som utført
    transition: done
    placement: primary
```

**Utvidelse av andres sider**

```yaml
extend: core.party-card
tabs:
  - label: Kontaktnotater
    list: { object: demo.contactnote, filter: { party: "{id}" }, fields: [followUp, text] }
fields:
  - after: core.party.email
    field: demo.contacts.preferredChannel
```

Kjernen slår sammen eierens side og alle utvidelser til én effektiv sidedefinisjon per tenant og sender den til klienten sammen med metadata og brukerens tillatelser. Klienten skjuler det brukeren ikke har tilgang til, men håndhevingen skjer i API-et.

**Klienten**

- Én SPA. Blazor WebAssembly (anbefalt for én person med .NET-bakgrunn, med MudBlazor) eller React – åpent valg; designet er uavhengig av det.
- Responsiv; godkjenning og kort skal fungere på mobil.
- Tekster er i18n-nøkler i appens `i18n/`-filer; nb og en i fase 1.
- Klienten snakker kun REST mot kjernen og har ingen forretningslogikk utover validering som speiler metadata.

**Bevisst utsatt:** egendefinerte komponenter fra apper, dashboards, rapportdesigner, personlig tilpasning av sider.

## Skanner-klient og frakoblet synk

Lagerarbeid passer ikke i den generiske klienten. Håndterminaler (Android, f.eks. Zebra og Honeywell) får en egen klienttype med skanning som primær input, store trykkflater, lyd/vibrasjon ved feil, og frakoblet modus. Selve skanner-klienten bygges i fase 3; **i fase 1 bygges bare kjernens API for frakoblet synk**, fordi det er dyrt å ettermontere.

**Skanner-klienten (fase 3, beskrevet for å sette kravene til kjernen)**

- Egen sidetype `scan`: en sekvens av steg (skann lokasjon → skann vare → antall → bekreft), definert deklarativt av lager- og WMS-appen.
- .NET MAUI-app eller PWA (åpent valg); hardware-skanner via Android-intents (Zebra DataWedge, Honeywell) og kamera som reserve.
- Etikettutskrift (ZPL) via dokumenttjenesten.

**Frakoblet synk-API (fase 1)**

| Del | Design |
| --- | --- |
| Nedlasting | Klienten abonnerer på et synk-sett definert av appen (f.eks. varer, lokasjoner og åpne plukkoppgaver for ett lager). Delta-synk på en monoton endringsmarkør per tenant fra outbox/audit, ikke tidsstempel |
| Opplasting | Handlinger lagres lokalt som en kø av **kommandoer** (f.eks. `ConfirmPick(task, location, item, qty)`), ikke ferdige rader. Hver kommando har klient-generert ID for idempotens |
| Behandling | Kjernen kjører kommandoene i rekkefølge gjennom vanlig data-API, hooks og tillatelser – samme validering som tilkoblet |
| Konflikter | Appen definerer regel per kommando: avvis, juster (avvik), eller manuell kø. `row_version` avgjør når det er konflikt |
| Reservasjon | Oppgaver tildeles enhet/bruker før frakobling, med utløp, slik at to terminaler ikke plukker samme oppgave |
| Sikkerhet | Kryptert lokal database; kortlevd token fornyes ved tilkobling; enheter kan sperres sentralt |

Kommandoer er valgt fordi lagerhandlinger er forretningshendelser (plukket, mottatt, flyttet), ikke feltendringer, og fordi det gjør konfliktløsning forståelig for brukeren.

**Bevisst utsatt:** selve skanner-klienten, ZPL-maler, sanntidsposisjonering, RFID.

## API

Én REST-flate generert fra metadata, med OpenAPI-beskrivelse per tenant (installerte apper avgjør hvilke objekter som finnes). Klienten og eksterne integrasjoner bruker samme API.

```
GET    /api/v1/{company}/sales.order?filter=status eq 'posted'&select=number,customer,total&top=50
GET    /api/v1/{company}/sales.order/{id}?expand=lines
POST   /api/v1/{company}/sales.order
PATCH  /api/v1/{company}/sales.order/{id}          (If-Match: row_version)
POST   /api/v1/{company}/sales.order/{id}/submit    (statusovergang)
POST   /api/v1/{company}/$batch                     (flere operasjoner i én transaksjon)
GET    /api/v1/metadata/objects, /pages, /permissions
GET    /api/v1/openapi.json
```

- Filter-syntaksen er et lite OData-lignende subsett (eq, ne, gt, lt, in, contains, and, or) som oversettes til samme filter-tre som data-API-et.
- Alle skriveoperasjoner krever `If-Match` med `row_version`; konflikt gir 412.
- `v1` i stien for kjerne-API-et; brytende endringer gir `v2` parallelt i minst ett år.
- Hastighetsbegrensning per tenant og per tjenestebruker fra start.

**Autentisering:** Bearer-token fra OIDC-leverandøren for brukere; OAuth 2.0 client credentials for tjenestebrukere. Begge byttes til kjernens interne token ved inngangen.

## Repo-struktur, utviklingsmiljø og leveranserekkefølge

| Repo | Synlighet | Innhold |
| --- | --- | --- |
| `erp-core` | Privat | Kjerne-API, app-vert, bakgrunnsarbeider, metadata, flyt, dokumenter, EF-migrasjoner, Helm-charts |
| `erp-client` | Privat | Den generiske klienten |
| `erp-sdk` | Offentlig (Apache 2.0) | `Erp.Sdk`, proto-kontrakter, manifest-JSON-skjema, `erp` CLI, dokumentasjon |
| `erp-apps` | Privat | Egne apper: `core.parties`, senere `finance`, `sales` osv. |
| `erp-samples` | Offentlig | Demo-appen `demo.contacts` og mal for ny app |

**Utviklingsmiljø:** Docker Compose med PostgreSQL, OIDC-leverandør, MinIO som S3, Mailpit, og kjernen. `erp dev` bruker samme compose-fil. CI på GitHub Actions. Ett Kubernetes-miljø (`dev`) i EU fra start.

**Leveranserekkefølge** – det vanskeligste (metadata og utvidelse) kommer først, mens det ennå er billig å endre. Detaljer i `docs/IMPLEMENTATION_PLAN.md`.

| Steg | Leveranse | Vises som | Linear |
| --- | --- | --- | --- |
| 1 | Kjerne-skjelett: tenant, selskap, land, valuta, bruker; RLS; OIDC | Logg inn, se tomt system | LUM-5 |
| 2 | Metadata og DDL; data-API med filter; audit-triggere | Objekt fra YAML, skriv og les via REST | LUM-6 |
| 3 | Manifest, pakkeformat, installasjon av deklarativ app | `core.parties` installert som app | LUM-7 |
| 4 | Generisk klient: liste og kort | Parter redigeres i nettleser | LUM-8 |
| 5 | Utvidelser av andres objekter og sider | Demo-appen legger felt på part | LUM-9 |
| 6 | App-vert, gRPC, hooks, `erp dev` | Demo-appen validerer og reagerer på hendelse | LUM-10 |
| 7 | Outbox, levering, webhooks, replay | Hendelse levert til app og webhook | LUM-11 |
| 7b | API for frakoblet synk | Frakoblede kommandoer synkes med konfliktløsning | LUM-15 |
| 8 | Statusmaskin, flyt, oppgaver, e-postgodkjenning, startside-unntak | To-stegs godkjenning ende til ende | LUM-12 |
| 9 | Dokumentmottak, filer, vedlegg, PDF | E-post med PDF knyttet til en part | LUM-13 |
| 10 | Oppgradering og avinstallasjon med data; metadata-øyeblikksbilder | Demo-app 1.0 → 1.1 og fjernet uten spor – porten for fase 1 | LUM-14 |

## Åpne designspørsmål for fase 1

- [ ] **OIDC-leverandør** (steg 1): Keycloak, Zitadel eller ekstern tjeneste?
- [ ] **Klientteknologi** (steg 4): Blazor WebAssembly eller React?
- [ ] **Grensen for deklarative apper** (steg 3): skal enkle regler (f.eks. «B er påkrevd når A er satt») kunne uttrykkes uten kode?
- [ ] **Hendelseslagring** (steg 7): 90 dager eller lengre?
- [ ] **E-postleverandør** (steg 9): Postmark, SendGrid EU, Scaleway TEM eller selvdriftet? Må ha EU-datasenter og innkommende e-post.
- [ ] **Objektlager** (steg 9): Hetzner Object Storage, Scaleway eller Azure Blob EU?
- [ ] **Skanner-klient** (fase 3, påvirker synk-API-et): .NET MAUI eller PWA?
- [ ] **Navn på produktet og SDK-et**: `Erp.Sdk`, `erp` og domener er plassholdere.
