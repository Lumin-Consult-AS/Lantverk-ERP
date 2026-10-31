# ADR-0027: SAF-T Regnskap (eksport)

Status: akseptert (31. oktober 2026). Innholdet skal gjennomgås av regnskapsfører (B2) og eksporten sendes gjennom Skatteetatens testløsning før porten lukkes.

## Beslutning

- **Versjon 1.40.** Skatteetatens skjema 1.40 (endret 30. april 2026) er obligatorisk fra regnskapsperioder som starter 1. januar 2027; 1.30 gjelder til da. Vi lager bare 1.40 (`AuditFileVersion` 1.40), siden det er det som gjelder når produktet tas i bruk. Det offisielle skjemaet (`Norwegian_SAF-T_Financial_Schema_v_1.40.xsd`, fra Skatteetatens GitHub-arkiv) ligger i `norway` som innebygd ressurs.
- **Eksporten ligger i `norway`**, ikke i `finance`: den er norsk. Den leser hovedboken gjennom data-API-et som en vanlig app.
- **Bakgrunnsjobb med nedlasting.** Objektet `norway.saftexport` (fra dato, til dato, status `requested`/`done`/`failed`, fil, melding, antall bilag). Å opprette posten ber om eksporten; en hendelse (`norway.saftexport.requested@1`) setter i gang jobben utenfor transaksjonen (en hook har bare 5 sekunder). Resultatet skrives tilbake på posten. Filen lagres som en vanlig fil (kilde «generated») og lastes ned fra `/api/v1/files/{id}/content`.
- **SDK og kjerne utvidet:** `IAppDocumentClient.StoreDocumentAsync` (gRPC `StoreDocument`) lar en app lagre en fil den har laget. Filen virusskannes som alle andre, og tillatelsen `core.file:create` kreves.
- **Valideres før levering.** Filen sjekkes mot det innebygde skjemaet etter at den er laget. En fil som ikke består, blir aldri lagt ut; posten får status `failed` og de første skjemafeilene som melding. Det er den samme tekniske kontrollen som Skatteetatens frittstående kontrollprogram (`SaftXmlValidator.exe`) gjør; den sjekker ikke innhold. Alle andre feil (mangler firmaopplysninger, konto uten grupperingskode, uventede feil) gir også status `failed` med en forklaring.
- **Innhold.** Header (programvare, firma med organisasjonsnummer, kontaktperson, adresse og MVA-registrering fra «Norsk oppsett», valgt periode, standardvaluta fra `finance.settings`); kontoer med grupperingskategori/-kode og inngående/utgående saldo; kunder og leverandører (deler fra reskontrokontoer) med saldo; MVA-tabell med koder, satser og gyldighet; alle bokførte bilag i perioden med linjer, kunde/leverandør, valuta og kurs. Saldo: balansekontoer har alt før perioden som inngående saldo, resultatkontoer bare regnskapsåret hittil.
- **MVA på linjene.** `TaxInformation` ligger på grunnlagslinjen (kode, sats, grunnlag, avgift på samme side), slik Skatteetatens eksempelfil har det. For en motpostering brukes det opprinnelige bilagets grunnlagslinjer på motsatt side. MVA-linjene selv har ingen `TaxInformation`.
- **Grupperingskode.** SAF-T 1.40 krever grupperingskategori og -kode (listen «næringsspesifikasjon») på hver konto, ikke standardkonto. Se ADR-0026.

## Utsatt

- Skalering: eksporten leser alle posteringer til og med perioden gjennom data-API-et og regner saldoene i minnet. Når en aggregering er tilgjengelig for apper bør saldoene hentes derfra.
- Betalingsbetingelser/forfall per linje og analysetyper (dimensjoner) i `Analysis`.
- Innsending til Skatteetaten (kommer med Skatteetaten-steget, B8) og gjentatte/planlagte eksporter.
- Nedlastingsknapp i klienten (filen hentes foreløpig med fil-id fra posten).
- Kontoer uten posteringer tas ikke med; kontoer med posteringer må ha grupperingskode.

## Konsekvenser

- Firmaopplysninger (firmanavn, organisasjonsnummer, kontaktperson) må fylles ut i «Norsk oppsett» før første eksport.
- Skjemaet 1.40 flyttes til en ny versjon av `norway` når Skatteetaten utgir en ny versjon.
