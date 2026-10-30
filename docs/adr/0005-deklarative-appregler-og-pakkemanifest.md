# ADR-0005: Deklarative feltregler, app-manifest og signert pakkeformat

- **Status:** Vedtatt
- **Dato:** 2026-10-30
- **Kontekst:** Steg 3 – Manifest og installasjon av deklarative apper (LUM-7)

## Kontekst og problemstilling

I Steg 3 skal apper kunne pakkes, valideres og installeres i en tenant uten kodeendringer i kjernen. Grunnappen `core.parties` (parter med roller kunde/leverandør, adresser og kontaktpersoner) skal bygges som første rent deklarative app (uten `runtime`-container).

Før implementering måtte vi ta stilling til («Beslutning først» i `docs/IMPLEMENTATION_PLAN.md`):
- Hvor mye logikk rent deklarative apper skal kunne uttrykke i manifestet uten egen kode-container.

## Beslutning

### 1. Grensen for logikk i rent deklarative apper
Rent deklarative apper (uten `runtime`-seksjon i `app.yaml`) støtter:
1. **Statiske feltbegrensninger:** `required`, `minValue`, `maxValue`, `maxLength`, `pattern` (regulært uttrykk) og `scale`.
2. **Betingede feltregler (`when`-uttrykk):** `requiredWhen`, `readOnlyWhen` og `visibleWhen` på hvert felt. Reglene uttrykkes med samme deklarative `when`-syntaks som filter-treet og flytmotoren (`field`, operator `eq`, `ne`, `gt`, `lt`, `ge`, `le`, `in`, `contains`, `isEmpty`, kombinert med `and`/`or`).
   - Kjernens data-API evaluerer `requiredWhen` og `readOnlyWhen` ved `Create` og `Update` på posten.
   - Den generiske klienten (Steg 4) evaluerer i tillegg `visibleWhen` (samt `requiredWhen` og `readOnlyWhen`) i brukergrensesnittet.
3. **Avgrensning mot kode-apper:** Utledede/beregnede verdier, validering med oppslag i andre objekter og sideeffekter krever en app med `runtime`-container og synkrone/asynkrone gRPC-hooks (Steg 6).

### 2. App-manifest (`app.yaml`) og JSON-skjema
- Manifestet (`app.yaml`) inneholder seksjonene `id`, `version`, `name`, `publisher`, `license`, `sdk`, `dependencies`, `runtime` (valgfri), `objects`, `extensions`, `enums`, `events`, `pages`, `permissionSets`, `migrations` og `attention`.
- JSON-skjemaet publiseres i `erp-sdk/schemas/app-manifest.schema.json` og eksponeres fra kjernen på `GET /api/v1/schemas/app-manifest.json`.
- Kjernen validerer manifestet både strukturelt mot skjemaet og semantisk mot tenantens tilstand:
  - Alle `dependencies` må være installert i tenanten med en versjon som tilfredsstiller semver-intervallet (f.eks. `>=1.0.0 <2.0.0`).
  - Egne objekter, enums, hendelser, sider og tillatelsessett må ha appens eget navneprefiks.
  - Utvidelser (`extensions`) kan kun peke på eksisterende objekter som ikke er merket `sealed: true`.
  - Abonnerte hendelser (`events.subscribes`) må være publisert av en installert app (eller kjernen).
  - `runtime.egress` må være en eksplisitt hviteliste av gyldige vertsnavn/URL-mønstre (ingen `*`-wildcards for åpne nettverk).

### 3. Signert pakkeformat og metadata-øyeblikksbilde
- En app-pakke er et zip-arkiv (`app.yaml`, `pages/*.yaml`, `permissions/*.yaml`, `i18n/*.yaml`) signert med HMAC-SHA256 / Ed25519-kompatibel pakkesignatur (`SIGNATURE` eller signatur-header). Kjernen verifiserer signaturen mot pakkeinnholdets SHA-256-sjekksum før pakken lagres i objektlageret (`IAppPackageStorage`).
- Ved hver installasjon, oppgradering og avinstallasjon oppretter kjernen et uforanderlig øyeblikksbilde (`core.metadata_snapshot`) av tenantens samlede aktive metadata (installerte apper, objekter, utvidelser, enums, hendelser, sider og tillatelsessett).

## Konsekvenser

- `core.parties` kan bygges som en 100 % deklarativ app i `erp-apps/core.parties` uten container, inkludert betinget regel om at `organizationNumber` er påkrevd når `partyType == 'organization'`.
- Både API og fremtidig klient bruker nøyaktig samme `when`-evalueringsmotor.
- Tenantens metadatahistorikk er reviderbar til enhver tid gjennom `core.metadata_snapshot`.

