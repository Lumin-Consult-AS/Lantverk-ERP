# ADR-0031: EHF (PEPPOL BIS Billing 3.0)

Status: akseptert (1. november 2026) for format, kontrakt og flyt. **Aksesspunktet er ikke koblet til ennå**; B6 er valgt til Storecove, men sandkassen er ikke tatt i bruk. Planens «Ferdig når» (sendt og mottatt via aksesspunktets testmiljø) er derfor **ikke oppfylt**.

## Beslutning

- **B6: Storecove** er valgt som aksesspunkt (1. november 2026). Det ligger bak `IAccessPoint` (`SendAsync`, `ReceiveAsync`, `AcknowledgeAsync`), slik at leverandøren kan byttes uten å røre formatet eller regnskapet. Storecove-adapteren er **ikke skrevet**: API-dokumentasjonen ligger bak innlogging i sandkassekontoen, og jeg har ikke gjettet på feltnavn. Til adapteren finnes, kjører appen med `NotConfiguredAccessPoint`: ingenting sendes eller hentes, og fakturaen feiler med en tydelig melding.
- **`ehf`-appen** (egen app, avhengig av `finance` og `norway`) lager og leser formatet. Den kjenner norske regler (organisasjonsnummer under skjema 0192, `NO…MVA`, selskapsdata fra Norsk oppsett), så den hører til ved siden av Norge-pakken og ikke i `finance`.
- **Utgående faktura i `finance`** (`finance.salesinvoice` + `finance.salesinvoiceline`) er laget i sin enkleste form: kunde, linjer (antall, pris, inntektskonto, MVA-kode), nummerserie, bokføring til bilag med kundepost. Salgsappen i fase 3 får ordre og priser. Skatten på hver linje settes av `norway` (`vatRate`, `vatAmount`, utgående MVA-kode, samme avrunding som MVA-linjen på bilaget); `finance` summerer bare. KID kommer fra `norway` og settes på fakturaen etter bokføring.
- **Bokføring skjer i en egen handler**, ikke inne i statusovergangen: `post` går til `posting` og publiserer `finance.salesinvoice.post.requested@1`; handleren lager bilaget, bokfører det og setter fakturaen til `posted` (eller tilbake til `draft` med årsak). Grunnen er en feil i kjernen (under).
- **Sending:** `send` (`posted` → `sending`) publiserer `finance.salesinvoice.send.requested@1`. `ehf` bygger UBL 2.1 Invoice med `CustomizationID urn:cen.eu:en16931:2017#compliant#urn:fdc:peppol.eu:2017:poacc:billing:3.0` og `ProfileID urn:fdc:peppol.eu:2017:poacc:billing:01:1.0`, kontrollerer det, lagrer det som fil på fakturaen og sender det via aksesspunktet med faktura-id som idempotensnøkkel (hendelser leveres minst én gang). Summen i dokumentet (MVA per kategori fra grunnlaget, som BIS-reglene sier) må være lik den bokførte, ellers sendes ingenting. Feil gir `failed` med årsak; `retry` sender på nytt.
- **Mottak** går på en tidsplan (`ehf.receive`, hvert 15. minutt) og kan kjøres for hånd. Hvert dokument lagres som fil og blir en `finance.supplierinvoice` som allerede er lest (`extractor = ehf`, så fakturatolken lar den være), med linjer, og foreslås som alle andre (leverandør på organisasjonsnummer, kontering fra forrige faktura, MVA-kode fra satsen). Kreditnotaer leses med negative beløp. Avrundingsforskjeller på inntil 5 øre mellom linjenes MVA og dokumentets total legges på siste linje. Et dokument som ikke kan leses blir en faktura i `failed`. Aksesspunktet får bekreftelse først når en faktura finnes; det samme dokumentet to ganger gir én faktura (filen er dedupert på innhold).

## Kontroller og validering

- Generert UBL er validert mot OASIS' offisielle UBL 2.1-skjema (`UBL-Invoice-2.1.xsd`) i testene.
- **PEPPOL-Schematron (BIS-reglene og norske regler) er ikke kjørt**; den krever XSLT 2.0. Vi sjekker selv det viktigste (kundens referanse, adresser, organisasjonsnumre, MVA-kode på alle linjer, summene) og gir norske feilmeldinger, men ikke-oppdagede brudd mot Schematron-reglene er mulige. Valider mot aksesspunktets testmiljø eller en offentlig validator før pilot.

## Funn i kjernen (ikke undersøkt eller rettet her)

Første versjon bokførte bilaget inne i statusovergangen `post` på salgsfakturaen. Da hang kallet til tidsavbruddet (30 s) og transaksjonen rullet tilbake (`ObjectDisposedException` på transaksjonens port, deretter `NpgsqlOperationInProgressException`). Det som skiller dette fra de andre overgangene som bokfører bilag (`accept` på banktransaksjoner, `post` på leverandørfakturaer) er at bilaget her lager en **kundepost**, som `norway` svarer på med KID i samme transaksjon: tre nivåer hook-i-hook i én transaksjon. Årsaken er ikke undersøkt; dette er en mistanke, ikke en diagnose. Omgått ved å bokføre i en hendelseshandler. Kjernens håndtering av dype hook-kjeder bør undersøkes før flere apper kjeder overganger slik.

## Utsatt

- Storecove-adapter (krever sandkassedokumentasjonen), registrering av selskapets PEPPOL-id hos aksesspunktet, oppslag i PEPPOL-katalogen (SMP) for å se om en kunde kan motta EHF før sending, og leveringsstatus/kvittering (MLR/IMR) utover referansen.
- Kreditnota som EHF, selskap som ikke er MVA-registrert, fremmed valuta, rabatter og gebyrer på dokumentnivå, vedlegg i EHF, og andre skjema enn organisasjonsnummer (0192).
- Adresse på kunden: kunden sendes uten postadresse (bare land), fordi parter ikke har adresse ennå.
- Utskrift av faktura som PDF med KID, og sending på e-post som alternativ.
- En bokføring som feiler midt i kan etterlate et utkast til bilag; fakturaen går tilbake til utkast, og et nytt forsøk lager et nytt bilag.

## Konsekvenser

- EHF kan ikke sendes eller mottas for ekte før en adapter er skrevet og testet i aksesspunktets testmiljø. Det er det som gjenstår for porten (punkt 2).
- `finance` har fått utgående faktura. Det er en minimal versjon som salgsappen senere skal bygge videre på eller erstatte.
