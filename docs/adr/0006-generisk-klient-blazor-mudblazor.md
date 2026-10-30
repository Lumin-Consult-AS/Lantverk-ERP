# ADR-0006: Generisk UI-klient med Blazor WebAssembly og MudBlazor

- **Status:** Vedtatt
- **Dato:** 2026-10-30
- **Kontekst:** Steg 4 – Generisk klient (LUM-8)

## Kontekst og problemstilling

I Steg 4 skal den generiske UI-klienten i `erp-client` bygges. Klienten skal rendre deklarative sidedefinisjoner (`list`, `card`, `dialog`, `worklist`) fra installerte apper uten noen domenespesifikk kode (ingen hardkodede skjermbilder for parter, ordrer eller regnskap).

Før implementering måtte vi velge («Beslutning først» i `docs/IMPLEMENTATION_PLAN.md`):
- Blazor WebAssembly (med MudBlazor) eller React.

## Beslutning

### 1. Blazor WebAssembly (.NET 10) med MudBlazor
- `erp-client` bygges som en frittstående Blazor WebAssembly-applikasjon (`Erp.Client`) med MudBlazor som komponentbibliotek.
- Dette gjør at hele kodebasen (`erp-core`, `erp-sdk`, `erp-client`) bruker C#/.NET 10, deler testverktøy (`xUnit` + `bUnit`) og kan vedlikeholdes effektivt av én utvikler.
- Klienten har **kun** HTTP/REST-avhengighet mot kjernen (`/api/v1/*`) og ingen direkte referanse til `Erp.Core.Infrastructure` eller databasen.

### 2. Metadata-drevet sidemotor
- Ved oppstart og innlogging henter klienten sesjonskontekst (`/api/v1/session`), effektive sider (`/api/v1/metadata/pages`), objekter (`/api/v1/metadata/objects`), enums (`/api/v1/metadata/enums`), tillatelsessett (`/api/v1/metadata/permissions`) og språkfiler (`/api/v1/metadata/i18n/{locale}`).
- **Sidetyper i fase 1:**
  - `list`: tabell med søk, OData-lignende filter, kolonne-sortering, valg av flere rader for massehandlinger (`$batch` sletting/handling) og navigasjon til tilhørende `card`-side.
  - `card`: skjemavisning med feltgrupper (`groups`), faner med relaterte underlister (`tabs` med filter `{ party: "{id}" }` og mulighet til å opprette/redigere underobjekter i dialog), samt statushandlinger (`actions` koblet til `transition`).
  - `dialog`: modal dialog for hurtigoppretting/redigering av rader (bl.a. fra underlister som adresser og kontaktpersoner) eller bekreftelse av handlinger.
  - `worklist`: oppgaveliste / arbeidsliste for elementer som krever brukerens oppfølging.

### 3. Klientvalidering og i18n (`nb` og `en`)
- Klientens valideringsmotor (`ClientFieldValidator`) speiler objektets metadata (`required`, `requiredWhen`, `readOnlyWhen`, `visibleWhen`, `maxLength`, `minValue`, `maxValue`, `pattern`). Selve håndhevingen skjer alltid i kjernens data-API.
- Alle etiketter løses opp via `I18nService` med støtte for bokmål (`nb`, standard) og engelsk (`en`), samlet fra alle installerte appers `i18n/`-ressurser pluss kjernens egne UI-tekster.

## Konsekvenser

- Enhver ny deklarativ app (som `core.parties` og senere `demo.contacts`) får fullt fungerende navigasjon, listeside, kortside med underlister, validering og oversettelse i nettleser uten kodeendring i `erp-client`.
- Kjerne-API-et kan også betjene de statiske Blazor WebAssembly-filene direkte under lokal utvikling og i demo-miljøet, slik at hele løsningen kan åpnes rett i nettleseren mot én adresse.

