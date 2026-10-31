# Implementeringsplan – fase 1: kjernen

Selvstendig plan for en kodeagent. Fase 1 bygger kjernen, app-rammeverket, flytmotoren, dokumentmottaket, frakoblet synk og den generiske klienten. Ingen domeneapper (regnskap, salg, lager) bygges i fase 1, bortsett fra grunnappen `core.parties` og demo-appen `demo.contacts`.

**Porten for fase 1:** demo-appen `demo.contacts` kan installeres i en tenant, legge et felt på `core.party`, publisere og abonnere på en hendelse, vise liste- og kortside i klienten, oppgraderes 1.0 → 1.1 og avinstalleres – uten kodeendring i kjernen. I tillegg kan en flyt med to godkjennere defineres og kjøres, et dokument mottas i innboksen, og endringer gjort frakoblet synkes med konfliktløsning.

Stegene skal tas i rekkefølge. Hvert steg har: mål, innhold, «Ferdig når» (akseptansekriterier), og eventuell beslutning som må tas først.

---

## Steg 1 – Kjerne-skjelett (LUM-5)

**Mål:** Innlogging mot et tomt, tenant-isolert system.

**Innhold**
- ASP.NET Core-løsning i `erp-core`; EF Core mot PostgreSQL; Docker Compose med PostgreSQL, OIDC-leverandør, MinIO, Mailpit.
- Kjerneobjekter: tenant, selskap (juridisk enhet), land, valuta, bruker.
- Obligatoriske kolonner på alle tabeller: `id uuid`, `tenant_id uuid`, `company_id uuid null`, `created_at`, `created_by`, `updated_at`, `updated_by`, `row_version bigint`.
- RLS-policy på alle tabeller (`tenant_id = current_setting('app.tenant_id')::uuid`). Applikasjonsrolle uten `BYPASSRLS`; egen rolle for migrasjoner.
- OIDC-innlogging; kjernen bytter eksternt JWT til et internt, kortlevd token (tenant, bruker, roller, selskap).
- CI (GitHub Actions): bygg, test, container-image.

**Ferdig når**
- Bruker logger inn og ser et tomt system.
- Test beviser at en spørring uten tenant-filter i koden likevel kun returnerer egen tenants data.

**Beslutning først:** OIDC-leverandør (Keycloak, Zitadel eller ekstern).

---

## Steg 2 – Metadata, DDL og data-API (LUM-6)

**Mål:** Objekter definert som data blir til tabeller og kan leses/skrives via REST.

**Innhold**
- Metadatamodell: objekt (`app.objekt`, eid av én app), felt, relasjon, nøkkel, enum, grensesnitt, hendelse.
- Felttyper: string, text, integer, decimal (med skala), boolean, date, datetime, uuid, reference, enum, json, money (beløp + valuta), blob-referanse.
- Ett databaseskjema per app, én tabell per objekt; felt fra andre apper blir egne kolonner (ikke JSON).
- DDL genereres av kjernen uten lange låser (`add column … null`, `create index concurrently`).
- Generisk data-API: Get, Query, Create, Update, Delete, Batch over `Record`; filter-tre (felt, operator, verdi, og/eller). Optimistisk låsing med `row_version`.
- `<tabell>_audit` (gammel/ny rad som JSONB, bruker, tid) fylt av databasetrigger; append-only.

**Ferdig når**
- Et objekt definert i en YAML-fil blir til en tabell, og rader kan skrives og leses via REST.
- Hver endring finnes i audit-tabellen, og applikasjonsrollen kan ikke endre eller slette audit-rader.

---

## Steg 3 – Manifest og installasjon av deklarative apper (LUM-7)

**Mål:** Apper installeres fra pakker, ikke hardkodes.

**Innhold**
- `app.yaml` valideres mot publisert JSON-skjema. Seksjoner: id, version, dependencies, runtime (valgfri), objects, extensions, enums, events, pages, permissionSets, migrations, attention.
- Regler ved installasjon: avhengigheter innenfor versjonsintervall; eget prefiks på objekter og felt; utvidelser kun på objekter som ikke er `sealed`; abonnerte hendelser må finnes; `egress` er hviteliste.
- Pakke: signert zip (`app.yaml`, `pages/`, `permissions/`, `i18n/`, referanse til container-image), lagret i objektlager.
- Øyeblikksbilde av tenantens samlede metadata ved hver installasjon/oppgradering.
- Grunnappen `core.parties` (part med roller kunde/leverandør, adresser, kontaktpersoner) bygges som første deklarative app.

**Ferdig når** `core.parties` installeres som app i en tenant og kan avinstalleres igjen.

**Beslutning først:** hvor mye logikk deklarative apper kan ha uten kode (f.eks. betingede påkrevd-regler).

---

## Steg 4 – Generisk klient (LUM-8)

**Mål:** Sider definert i manifestet vises og fungerer i nettleser.

**Innhold**
- SPA i `erp-client` som kun snakker REST mot kjernen.
- Sidetyper: `list` (filter, sortering, søk, massehandlinger), `card` (grupper, faner, underlister), `dialog`, `worklist`.
- Handlinger kobles til statusoverganger.
- Klientvalidering speiler metadata (påkrevd, type, lengde); håndheving skjer i API-et.
- Responsiv; i18n-nøkler med nb og en.

**Ferdig når** parter kan opprettes, listes og redigeres i nettleser.

**Beslutning først:** Blazor WebAssembly (anbefalt for én .NET-utvikler, med MudBlazor) eller React.

---

## Steg 5 – Utvidelser av andres objekter og sider (LUM-9)

**Mål:** En app kan utvide en annen app uten at den andre endres.

**Innhold**
- `extensions` i manifestet: felt på andres objekter (implisitt prefiks med eierapp).
- Sideutvidelser: `extend`, `tabs`, `fields` med `after`-plassering. Kjernen slår sammen eier-side og alle utvidelser til én effektiv side per tenant.
- Eier kan ikke endre andres felt og omvendt.
- Avinstallasjon: felt fjernes fra metadata; kolonnen beholdes som foreldreløs i en karensperiode.
- Demo-appen `demo.contacts` i `erp-samples`: objektet `demo.contactnote`, feltet `preferredChannel` på `core.party`, fanen «Kontaktnotater» på partkortet.

**Ferdig når** `demo.contacts` viser sitt felt og sin fane på partkortet, og avinstallasjon fjerner dem fra UI.

---

## Steg 6 – App-vert, gRPC og SDK (LUM-10)

**Mål:** Apper med kode kjører isolert i egne containere.

**Innhold**
- App-vert starter én container per app-versjon via Kubernetes-API (delt av alle tenanter), med tidsfrist (5 s for synkrone kall) og ressursgrenser.
- Protobuf-kontrakter i `erp-sdk`: `AppService` (kjerne → app) og `DataService`, `EventService`, `WorkflowService`, `DocumentService` (app → kjerne).
- Hvert kall bærer kontekst: tenant, selskap, bruker, korrelasjons-ID og kortlevd token som appen må sende tilbake.
- Hooks: `OnValidate`, `OnBeforeSave`, `OnAfterSave` (synkrone), `OnEvent` (asynkron, idempotent), `Implements<T>` (grensesnitt).
- Transaksjon spenner over ett kall. Nettverkspolicy fra `egress`.
- `Erp.Sdk` NuGet og `erp dev` CLI: lokal kjerne i Docker, test-tenant, hot reload av manifest og sider.

**Ferdig når** `demo.contacts` avviser et notat med oppfølgingsdato i fortiden (`OnValidate`) og sletter notater når en part slettes (`OnEvent`), fra egen container.

---

## Steg 7 – Hendelsesbuss (LUM-11)

**Mål:** Pålitelig, løs kobling mellom apper og ut mot omverdenen.

**Innhold**
- Transaksjonell outbox i PostgreSQL; bakgrunnsarbeider leverer.
- Hendelser `app.objekt.hendelse@versjon`, med tenant, selskap, bruker, korrelasjons-ID og tidspunkt.
- Egen leveringskø per abonnent med forsøk, eksponentiell backoff og dødbrevkø.
- Minst én gang; rekkefølge per aggregat-ID. Idempotens-hjelper i SDK.
- `core.event_log` med replay til valgt app.
- Webhooks utad, signert med tenantens hemmelighet.

**Ferdig når** en hendelse er synlig i loggen, levert til demo-appen og til en webhook, og kan spilles av på nytt.

**Beslutning først:** lagringstid for hendelser (90 dager eller lengre).

---

## Steg 7b – API for frakoblet synk (LUM-15)

**Mål:** Grunnlag for skanner-klienten på håndterminaler i fase 3. Selve skanner-klienten bygges ikke nå.

**Innhold**
- Synk-sett definert av app (f.eks. varer, lokasjoner, åpne oppgaver for ett lager); delta-synk på en monoton endringsmarkør per tenant (fra outbox/audit), ikke tidsstempel.
- Opplasting som kø av **kommandoer** (f.eks. `ConfirmPick(task, location, item, qty)`) med klient-generert ID for idempotens – ikke rå radendringer.
- Kommandoer kjøres i rekkefølge gjennom vanlig data-API, hooks og tillatelser.
- Konfliktregel per kommando definert av appen: avvis, juster (avvik) eller manuell kø; konflikt avgjøres med `row_version`.
- Reservasjon av oppgaver til enhet/bruker før frakobling, med utløp. Kortlevd token fornyes ved tilkobling; enheter kan sperres sentralt.

**Ferdig når** en testklient laster ned et synk-sett, går frakoblet, utfører kommandoer, kobler til igjen, og kjernen behandler dem med samme validering som tilkoblet – inkludert én konflikt som løses etter appens regel.

---

## Steg 8 – Statusmaskin, flyt, oppgaver og startside-unntak (LUM-12)

**Mål:** Godkjenningsflyter kunden kan konfigurere selv, og en startside som viser det som krever handling.

**Innhold**
- `status` i manifestet: initial, transitions (from, to, permission, workflow, locksRecord).
- Flytdefinisjon som tenantens data (redigeres i et skjema i klienten): steg med mottaker (bruker, rolle eller dimensjonseier), `when`-regler (eq, ne, gt, lt, in, isEmpty; og/eller), sekvensiell eller parallell, frist og eskalering, delegering, `onComplete`/`onReject`.
- `core.task` med mottaker, frist og lenke; vises i worklist; godkjenn/avvis fra e-post via signert lenke.
- Objektet låses under flyt, unntatt felt merket `editableInWorkflow`. Hvert steg logges i revisjonssporet. Hendelser `core.workflow.started/stepcompleted/completed@1`.
- `attention` i manifestet: unntak med kilde (deklarativ spørring eller `OnAttention`-hook), alvorlighet (action, warning, info), målgruppe via tillatelser og handling. Kjernen cacher tellinger per bruker og oppdaterer på hendelser. Brukeren kan skjule/utsette.

**Ferdig når** en to-stegs godkjenning med beløpsgrense kjøres ende til ende, og «Til godkjenning» vises på startsiden for riktig bruker.

---

## Steg 9 – Dokumentmottak (LUM-13)

**Mål:** Innkommende dokumenter havner i systemet og kan knyttes til objekter.

**Innhold**
- `core.file` (uforanderlig, SHA-256, i objektlager), `core.inbox` (unik e-postadresse per selskap og kanal), `core.inboxitem` (status new/claimed/processed/rejected), `core.attachment` (fil ↔ objekt, med kategori).
- Innkommende e-post via webhook → `core.inboxitem` → hendelse `core.inbox.received@1`. Apper kan «claime» elementer; resten vises som manuell arbeidsliste.
- Virusskanning (ClamAV) før tilgjengeliggjøring; dedupering med varsel.
- Oppbevaringsregler per kategori (bilag kan ikke slettes i oppbevaringsperioden; 5 år standard, per land).
- PDF fra HTML-mal (Playwright/Chromium i egen container); utsending på e-post.

**Ferdig når** en e-post med PDF havner i innboksen og kan knyttes til en part.

**Beslutning først:** e-postleverandør (EU-datasenter, innkommende e-post) og objektlager.

---

## Steg 10 – Oppgradering og avinstallasjon med data (LUM-14)

**Mål:** Apper kan utvikle seg over tid uten nedetid for kundene. **Dette er porten for fase 1.**

**Innhold**
- `migrations` i manifestet: C# mot data-API, kjørt per tenant med fremdriftslogg og gjenopptak.
- Semver håndheves: minor er bakoverkompatibel; major krever migrasjon, og kjernen nekter oppgradering når avhengige apper ikke støtter ny versjon.
- Avinstallasjon arkiverer data i en definert periode; foreldreløse kolonner droppes etter karens.
- Metadata-øyeblikksbilder kan spørres («hva så skjemaet ut som da»).

**Ferdig når** hele porten for fase 1 (øverst i dokumentet) demonstreres i en demo-tenant, og `erp-samples` publiseres offentlig.

> **Endret 31. oktober 2026 (ADR-0015):** publiseringen av `erp-samples` er utsatt til en senere port. Porten for fase 1 lukkes når den øvrige demoen er godkjent.

---

## Utsatt (skal ikke bygges i fase 1)

WebAssembly-sandkasse, per-tenant-containere, mikrotjenester, multi-region, beregnede felt, objektarv, tillatelser på feltnivå, visuell flytdesigner, parallelle flytgrener, eksakt-én-gang-levering, dashboards, rapportdesigner, egendefinerte klientkomponenter, AI-tolkning, EHF/PEPPOL, OCR, selve skanner-klienten, SDK for andre språk enn C#.
