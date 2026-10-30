# ADR-0007: Utvidelse av andres objekter og sider samt karensperiode for foreldreløse kolonner

- **Status:** Vedtatt
- **Dato:** 2026-10-30
- **Kontekst:** Steg 5 – Utvidelser av andres objekter og sider (LUM-9)

## Kontekst og problemstilling

I Steg 5 skal en app kunne utvide en annen apps objekter (`extensions`) og sider (`pages` med `extend`) uten at eierappen endres. Demo-appen `demo.contacts` i `erp-samples/demo.contacts` skal:
- definere objektet `demo.contactnote`
- legge feltet `preferredChannel` (enum `demo.channel`) på `core.party`
- legge feltet `demo.contacts.preferredChannel` etter `core.party.email` og fanen «Kontaktnotater» på `core.party-card`
- kunne avinstalleres slik at feltet og fanen forsvinner fra aktiv metadata og UI, mens databasekolonnen beholdes som foreldreløs i en karensperiode.

## Beslutning

### 1. Objektutvidelser og eierskapsvern
- Felt som legges til via `extensions` får implisitt den utvidende appens ID som `ownerApp` (f.eks. `demo.contacts`).
- I REST-API og UI-metadata eksponeres utvidelsesfelt som `<ownerApp>.<fieldName>` (f.eks. `demo.contacts.preferredChannel`), og lagres i eierobjektets fysiske PostgreSQL-tabell som en egen kolonne med prefiks `<owner_app_snake>_<field_snake>` (f.eks. `demo_contacts_preferred_channel` på `core.party`).
- **Gjensidig eierskapsvern:**
  - Eierappen av et objekt kan aldri endre, overskrive eller slette felt som eies av en annen app.
  - En utvidende app kan aldri endre eller overskrive eierappens egne felt eller en tredje apps utvidelsesfelt.

### 2. Sammenslåing av sideutvidelser
- Sidedefinisjoner med `extend: <pageId>` registreres som `PageExtensionDefinition` i appens manifest.
- `AppInstallationService.GetEffectivePagesAsync()` slår sammen eierens grunnside med alle installerte utvidelser i tenanten:
  - `fields` med `after: <targetField>` settes inn umiddelbart etter målfeltet i riktig feltgruppe.
  - `tabs` legges til i kortsidens faneliste med filtermal (f.eks. `{ party: "{id}" }`).

### 3. Avinstallasjon og foreldreløse kolonner i karensperiode
- Når en utvidende app avinstalleres:
  - Utvidelsesfeltene fjernes umiddelbart fra `ObjectDefinition.Fields`, og sideutvidelsene forsvinner fra `GetEffectivePagesAsync()`.
  - De fysiske databasekolonnene droppes **ikke** ved avinstallasjon. I stedet registreres de i `ObjectDefinition.OrphanedColumns` med `OrphanedAt` og `DropAfter` (standard 30 dagers karensperiode).

## Konsekvenser

- To apper kan legge til felt med samme lokale navn (f.eks. `discount` eller `preferredChannel`) på samme objekt uten navnekollisjon i metadata eller SQL.
- Utilsiktet avinstallasjon fører ikke til umiddelbart datatap i PostgreSQL, mens UI og API straks reflekterer at appen er fjernet.

