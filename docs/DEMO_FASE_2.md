# Demo av porten for fase 2

Porten (se `docs/IMPLEMENTATION_PLAN_FASE_2.md`) har seks punkter. Denne veiledningen viser hva som er bevist, hvordan du kjører beviset, og hva som **ikke** er oppfylt ennå. Den skal ikke leses som at porten er lukket: den er det ikke (se «Status» under).

Appene i fase 2 (`finance`, `norway`, `bank.iso20022`, `invoice.claude`, `ehf`) har egen kode og kjører i egne containere på Kubernetes. Som for `demo.contacts` i fase 1 finnes det ikke et lokalt oppsett som starter dem for hånd, så beviset er de automatiske testene. De starter kjernen, appene (i prosess, bak samme gRPC-grensesnitt som i drift), en ekte PostgreSQL i Testcontainers og en lagring i minnet.

## Kjør beviset

Fra rotmappen, med Docker i gang:

```powershell
cd erp-core
$env:ERP_TEST_S3_IMAGE = "pgsty/minio:latest"
dotnet test tests/Erp.Core.Tests
```

Forventet: alle tester grønne (118 da dette ble skrevet). Hele kjøringen tar rundt fire–fem minutter. Et enkelt område kjører du med `--filter "FullyQualifiedName~<navn>"`, for eksempel `~InvoiceTests`.

## Status per portpunkt

| # | Portpunkt | Status | Bevis |
| --- | --- | --- | --- |
| 1 | SAF-T-eksport validert mot Skatteetatens testverktøy | **Delvis.** Eksporten lages i bakgrunnen og består Skatteetatens skjema (XSD v1.40) og offisielle kodelister. **Testverktøyet er ikke kjørt.** | `NorwayTests`: `ASaftExport_IsMadeInTheBackground_PassesSkatteetatensSchema_AndCarriesBalancesVatAndReversals`, `TheChartAndTheVatCodes_AgreeWithSkatteetatensOfficialLists` |
| 2 | EHF sendt og mottatt via aksesspunkt | **Delvis.** EHF/PEPPOL BIS 3.0 lages og leses, består UBL 2.1-skjemaet, og flyten (utgående faktura, sending, mottak til bilagsforslag) er testet mot et aksesspunkt i minnet. **Ingen adapter mot Storecove er skrevet, og ingenting er sendt for ekte.** | `EhfTests` (alle) |
| 3 | En regnskapsfører har testet bokføringen | **Ikke gjort.** Testopplegget er skrevet: `docs/REGNSKAPSFORER_TEST_FASE_2.md`. | – |
| 4 | Skannet leverandørfaktura fra innboks via tolk og flyt til bokført bilag og betalt via bank | **Delvis.** Hele kjeden er testet, men tolken er en fake i testen: **Claude via Bedrock er ikke prøvd mot en ekte konto**. «Betalt via bank» betyr at betalingsfilen (pain.001) er laget og validert; filen leveres til banken av en person. | `InvoiceTests`, `BankTests` |
| 5 | En innbetaling med KID avstemmes automatisk | **Oppfylt** (mot bankfiler i ISO 20022-format; ikke mot en ekte bank). | `ReconciliationTests`: `APaymentWithKid_SettlesTheRightCustomerItem_WithoutManualWork_AndTheRestWaitsForReview` |
| 6 | Ingen norsk logikk utenfor Norge-pakken | **Oppfylt for kjernen, SDK-et, de generelle appene og regnskapsappen**, med en tolkning av adapterne (se under). | `NorwayTests`: `NothingNorwegian_IsInTheCore_TheSdk_TheGenericApps_OrTheLedger`, `TheGeneralLedger_HasNoNorwegianValues_AndDoesNotKnowThePackage` |

I tillegg, utover portpunktene: MVA-melding i Skatteetatens format er generert og validert (`VatMeldingTests`), men **ikke sendt** (venter på B8).

### Om punkt 6 og adapterne

Sjekken leser all kode og alle manifester i `erp-core`, `erp-sdk`, `core.*`-appene og `finance`, og feiler på ord som MVA, SAF-T, KID, EHF, Skatteetaten, Altinn, PEPPOL, «norsk» og NOK (kommentarer, oversettelser, migreringer og demotenantens startdata er ikke kode). Den fant to etiketter i `finance` med «KID», og et mønster i `core.parties` som tvang norske organisasjonsnumre (ni sifre); begge er rettet, og regelen for organisasjonsnummer ligger nå i Norge-pakken.

Fire apper er bevisst **ikke** med i sjekken, fordi de er adaptere mot en bestemt tjeneste og ikke del av regnskapet: `fx.norgesbank` (Norges Banks kurser), `bank.iso20022` (bankfiler; KID står som referansetype i betalingsfilen), `invoice.claude` (fakturatolken) og `ehf` (PEPPOL, som bruker organisasjonsnummer og norsk MVA-registrering). Dette er en tolkning av «ingen norsk logikk utenfor Norge-pakken»: arkitekturen sier at integrasjoner er adaptere bak en kontrakt. Er du uenig i tolkningen, er `ehf` det eneste som egentlig bør flyttes inn i Norge-pakken.

## Se for deg gangen i testene

1. **Kontoplan og MVA-koder** legges inn første gang et regnskapsår opprettes (`TheFirstFiscalYear_…`). Kontoer kan legges til, endres og fjernes (`TheChartOfAccounts_CanBeExtended…`).
2. **Bilag med MVA:** en linje med MVA-kode får automatisk MVA-linje, og bokføring lager uforanderlige poster med åpne poster for kunder og leverandører (`VatIsCalculatedOnLines_…`).
3. **Leverandørfaktura:** PDF i innboksen blir en faktura, foreslås med leverandør og kontering, sendes til godkjenning, og bokføres av en person (`InvoiceTests`).
4. **Betaling:** forfalte leverandørposter blir en betalingsbunt, godkjennes, frigis, og en pain.001-fil lages (`BankTests`).
5. **Innbetaling:** kontoutskrift (camt.053) leses inn, og innbetalinger med riktig KID og beløp utlignes av seg selv. Resten havner på startsiden (`ReconciliationTests`).
6. **Utgående faktura:** bokføres med KID, lages som EHF og sendes (`EhfTests`).
7. **MVA-oppgjør og MVA-melding:** oppgjøret per termin gir en melding i Skatteetatens format, med frist på startsiden (`TheVatReturn_…`, `VatMeldingTests`).
8. **SAF-T:** eksport per periode med nedlasting (`NorwayTests`).

## Det som mangler for å lukke porten

| Hva | Hvem/hva trengs | Berører |
| --- | --- | --- |
| Kjøre Skatteetatens SAF-T-testverktøy på en eksportert fil | Du kjører verktøyet (`SaftXmlValidator.exe`, lastet ned fra Skatteetaten) mot en fil fra en testtenant, eller sier at jeg kan kjøre det. | Punkt 1 |
| Storecove-adapter og en EHF sendt og mottatt i testmiljøet | API-dokumentasjon og testnøkkel fra Storecove-sandkassen | Punkt 2 |
| Regnskapsfører (B2) går gjennom `docs/REGNSKAPSFORER_TEST_FASE_2.md` | Navn på en autorisert regnskapsfører | Punkt 3 |
| En ekte tolking med Claude via Bedrock | Bedrock-nøkkel og modell-id i EU-region | Punkt 4 |
| Innsending av MVA-melding mot Skatteetatens testmiljø | B8 (systemleverandør, Maskinporten, systembruker) | Ikke et portpunkt, men planens steg 10 |

## Feilsøking

- **Testene feiler med Docker-feil:** start Docker Desktop. Testene starter en PostgreSQL i en container.
- **`ERP_TEST_S3_IMAGE` mangler:** S3-testene hoppes over (de er ikke bevis for fase 2), resten kjører.
- **Port opptatt eller gamle containere:** `docker ps` og stopp rester fra tidligere kjøringer.
