# ADR-0008: App-vert, gRPC-kontrakter og offentlig `Erp.Sdk`

- **Status:** Vedtatt
- **Dato:** 2026-10-30
- **Kontekst:** Steg 6 – App-vert, gRPC og SDK (LUM-10)

## Kontekst og problemstilling

I Steg 6 skal apper med forretningslogikk kunne kjøre isolert i egne containere og kommunisere med kjernen over gRPC gjennom et offentlig SDK (`Erp.Sdk` under Apache 2.0-lisens). Apper har aldri direkte databasetilgang; alle lese- og skriveoperasjoner må gå tilbake gjennom kjernens tjenester med brukerens kortlevde token.

## Beslutning

### 1. Protobuf-kontrakter (`erp-sdk/protos/erp_contracts.proto`)
- **Kjerne → App (`AppService`):**
  - `HealthCheck`: helsesjekk av app-container.
  - `InvokeHook`: synkrone livssyklushooks (`OnValidate`, `OnBeforeSave`, `OnAfterSave`) med 5 sekunders tidsfrist (`Deadline`).
  - `DeliverEvent`: asynkron hendelseslevering (`OnEvent`).
  - `InvokeInterface`: kall til grensesnitt-implementasjoner (`Implements<T>`).
  - `RunMigration`: kjøring av versjonsmigrasjoner per tenant.
- **App → Kjerne (`DataService`, `EventService`, `WorkflowService`, `DocumentService`):**
  - Hvert kall bærer `CallContextProto` (`tenant_id`, `company_id`, `user_id`, `correlation_id`, `access_token`, `today_iso`).
  - Kjernen validerer det kortlevde interne JWT-tokenet på hvert eneste tilbakekall fra appen, slik at appen aldri kan eskalere rettigheter eller krysse tenant-grenser.

### 2. Transaksjonskontekst på tvers av gRPC-kall
- Når kjernen kaller en synkron hook (`OnValidate`, `OnBeforeSave`, `OnAfterSave`), knyttes den pågående databasetransaksjonen til kallets `correlation_id`.
- Kaller appen tilbake til kjernens `DataService` med samme `correlation_id` og gyldig token, deltar operasjonen i samme transaksjon og rulles tilbake dersom hooken eller hovedoperasjonen feiler.

### 3. Container-orkestrering og nettverkspolicy (`egress`)
- App-verten starter én container per `(appId, version)` delt mellom tenanter, med CPU-/minnegrenser fra `runtime.resources`.
- Fra `runtime.egress` genereres en Kubernetes `NetworkPolicy` som kun tillater utgående trafikk til kjernen (gRPC) og eksplisitt hvitelistede endepunkter.

### 4. `Erp.Sdk` og `erp dev` CLI
- `Erp.Sdk` tilbyr `AppBase`, attributtene `[OnValidate]`, `[OnBeforeSave]`, `[OnAfterSave]`, `[OnEvent]` og `[Implements]`, samt `Record`, `ValidationResult` og `AppContext`.
- `erp` CLI (`Erp.Cli`) støtter `erp validate`, `erp pack` og `erp dev` (som overvåker arbeidskatalogen og hot-reloader manifest, sider og pakke mot en lokal kjerne).

## Konsekvenser

- `demo.contacts` bygges utelukkende mot `Erp.Sdk` uten noen referanse til `Erp.Core.*`.
- Synkrone hooks beskyttes av 5 sekunders tidsfrist, slik at en treg eller hengende app ikke blokkerer kjernen.

