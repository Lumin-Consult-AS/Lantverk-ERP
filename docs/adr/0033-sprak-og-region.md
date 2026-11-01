# ADR-0033: Språk og region

Status: akseptert (1. november 2026). B10 følger planens anbefaling: innstilling per bruker, med tenantens standard som utgangspunkt.

## Beslutning

- **Hvor innstillingen ligger.** Brukeren har to valgfrie koder i kjernen: `core.user.language` (`nb`, `en`) og `core.user.region` (landskode, to bokstaver). Tenanten har en standard: `core.tenant.default_language` og `core.tenant.default_country_code` (som fantes fra før og er landet tenanten er opprettet i; det er også standardregionen). En bruker som ikke har valgt får tenantens standard (`null` betyr «bruk standard»). Kjernen lagrer og validerer bare kodene (språk må være ett vi har tekster for, region må være to bokstaver); den vet ikke hvordan en region skriver dato og tall.
- **API.** `GET/PUT /api/v1/me/preferences` (eget valg; `null` eller tom verdi fjerner valget), `PUT /api/v1/tenant/preferences` (bare tenant-administrator). `/api/v1/session` gir brukerens `language`/`region` og de gjeldende `effectiveLanguage`/`effectiveRegion`. Valget lagres på serveren, så det følger brukeren mellom enheter og økter.
- **Formatering er klientens oppgave** (`RegionService`), ikke kjernens og ikke appenes. Klienten har en profil per region: `NO` (`dd.MM.yyyy`, `1 234,56` med hardt mellomrom, uke fra mandag), `GB` (`dd/MM/yyyy`, `1,234.56`) og `US` (`MM/dd/yyyy`, uke fra søndag). En region uten profil får ISO-datoer og desimalpunktum. Profilene er egen kode i stedet for nettleserens kulturdata, fordi Blazor WebAssembly som standard ikke har dem og de gir ulike resultater i ulike nettlesere.
- **Visning** følger typen på feltet (dato, tidspunkt, desimaltall, heltall, beløp) i lister, kort, faner, arbeidslister, korttitler, innboks, oppgaver og planlagte jobber. Heltall under 10 000 grupperes ikke (år og koder), desimaltall alltid. Tidspunkt vises i brukerens tidssone.
- **Inntasting.** Datofelt er et tekstfelt som leser datoen slik regionen skriver den (`31.12.2026`, `1.2.2026`, `31.12.26`), på kortform (`311226`, `31122026`) og som ISO (`2026-12-31`), og har en kalender (dialog) bak ikonet. Tallfelt leser mellomrom, komma som desimaltegn og punktum som tusenskille eller desimalpunkt slik folk skriver (`1 234,56`, `1.234,56`, `1234.56`, `12.5`). Gjentatt komma i norsk region (`1,2,3`) og feil gruppering avvises.
- **API, lagring, hendelser og eksport er uendret.** Alt som går til API-et er ISO 8601-datoer og tall med punktum; klienten gjør om til det. Tekst i et dato- eller tallfelt som regionen ikke kunne lese blir stående som tekst og **avvises i skjemaet før lagring** (ny melding «ikke en gyldig dato/ikke et gyldig tall»), så feil formatering aldri når data-API-et. API-et avviser fortsatt en dato skrevet på norsk (`31.12.2026`). Testene viser begge deler.
- **Språkvelgeren** i toppmenyen (NB/EN) er erstattet av en innstillingsside (`/settings`) med eget valg, forhåndsvisning av dato og beløp, og for administrator tenantens standard.

## Avgrensninger og kjente begrensninger

- **Klientens skjermbilder er ikke prøvd i en nettleser.** Formateringen og innlesingen er enhetstestet (41 tester), og serversiden er testet mot en ekte database, men komponentene (datofelt med kalender, innstillingssiden) er bare kompilert. Se dem med øyne før dette regnes som ferdig.
- **Filteruttrykk** som brukeren skriver i en liste (`status eq 'active'`) er et uttrykksspråk mot API-et og må fortsatt ha ISO-datoer og punktum.
- **Tekster som apper lager** (meldinger, notater, filnavn) kan inneholde ISO-datoer; de vises som de er.
- Bare `nb` og `en` som språk, og `NO`, `GB`, `US` som regioner med egen profil. Andre regioner får ISO-datoer. Flere regioner er en ny profil i klienten, uten endring i kjernen.
- Kalenderen bruker MudBlazors egen oversettelse av ukedager og måneder, som følger nettleseren.
- Regionen styrer ikke valuta, tidssone eller sortering.

## Konsekvenser

- Migrering `202611010001_LanguageAndRegion` legger til tre kolonner; eksisterende brukere får `null` og følger tenantens standard (`nb`, `NO`), så ingen ser noen endring før de velger.
