# ADR-0026: Norge-pakken (`norway`)

Status: akseptert (31. oktober 2026). Kontoplan, MVA-koder, satser og eksempler skal gjennomgås av regnskapsfører (B2).

## Beslutning

- `norway` er en kodet app (container) mot det offentlige SDK-et, med `finance` som eneste avhengighet. `finance` kjenner ikke `norway`: den henger seg på med **utvidelsesfelt**, **hooks** og **hendelser**. En test (`TheGeneralLedger_HasNoNorwegianValues_AndDoesNotKnowThePackage`) leter i `finance` etter norske verdier (NOK, MVA, SAF-T, NS 4102, kontonumre) og feiler hvis den finner noen.
- **Kontoplan som data.** NS 4102 (firesifret) ligger som CSV i appen (`Data/chart-of-accounts.csv`), med SAF-T-standardkonto per konto. Standardkontoen lagres som utvidelsesfelt `norway.standardAccount` på `finance.account`, slik at SAF-T-eksporten (steg 5) leser den derfra. Kontoplanen er et utvalg av de vanligste kontoene, ikke hele NS 4102.
- **Installasjon.** Når selskapet oppretter sitt første regnskapsår (`core.fiscalyear.created@1`) legger appen inn kontoplan og MVA-koder. Er regnskapsåret opprettet før `norway` ble installert, opprettes posten «Norsk oppsett» (`norway.settings`) som ber om det (`norway.setup.requested@1`). Innleggingen er en hendelse, ikke en hook, fordi den kan ta lengre tid enn fristen på 5 sekunder. Posten `norway.settings` husker versjonen (`chartVersion`): det som selskapet har endret eller slettet legges ikke tilbake, og en ny versjon av pakken legger bare til det som mangler. Det finnes ingen installasjons-hook i kjernen; hendelsene er den eksisterende veien.
- **Kontoplanen er selskapets.** Kontoer kan legges til, endres og fjernes (kontokort i `finance`, bokholder har `delete`; SAF-T-standardkonto vises på kortet av `norway`). En konto med posteringer kan ikke fjernes, siden hovedboken er uforanderlig; den gjøres inaktiv. En MVA-kodes konto kan fjernes (referansen krysser apper og har ingen fremmednøkkel), men koden avvises på bilagslinjer til den peker på en konto igjen.
- **MVA-koder.** `norway.vatcode` bruker Skatteetatens SAF-T-standardkoder. Første versjon har innenlandske koder og fritak: 1, 11, 12, 13 (inngående fradrag), 3, 31, 32, 33 (utgående), 5, 52, 6 (utgående uten avgift) og 7 (kjøp uten MVA-behandling). Hver kode har retning (utgående/inngående) og MVA-konto (2700/2710). **Satser** ligger i `norway.vatrate` med «gjelder fra» og «gjelder til», slik at en satsendring er en ny rad og ikke en kodeendring. Satsene er lagt inn fra 1. januar 2022.
- **MVA på bilagslinjer.** Linjen får utvidelsesfeltet `norway.vatCode`. Grunnlaget er nettobeløpet på linjen. Ved lagring lager appen (OnAfterSave) en egen bilagslinje for MVA-beløpet på kodens konto, på samme side som grunnlaget: nettobeløp × sats på bilagsdatoen, avrundet til to desimaler (bort fra null). Den linjen peker tilbake til grunnlagslinjen (`norway.vatSourceLine`) og følger den: endres grunnlaget, endres MVA-linjen; slettes grunnlaget, slettes MVA-linjen. Ved bokføring kontrollerer appen at hver MVA-linje stemmer med grunnlaget, så en hånd-endret MVA-linje avvises. Koden må være aktiv og ha en sats på bilagsdatoen.
- **MVA-posteringer.** Når et bilag er bokført (`finance.journal.posted@1`) lager appen `norway.vatentry` med grunnlag og avgift per grunnlagslinje og kode. Fortegn: utgående kode er positiv ved kredit, inngående ved debet. Et motpostert bilag gir den negerte motposten av det opprinnelige (`rev:<id>`). Kilden er unik, så levering mer enn én gang gir ingen duplikater.
- **MVA-oppgjør per termin.** `norway.vatreturn` har fra- og tildato. Overgangen `calculate` summerer MVA-posteringene i terminen per kode (`norway.vatreturnline`: grunnlag og avgift) og setter utgående, inngående og beløp å betale (utgående minus inngående; negativt = til gode). `reopen` fjerner linjene. Terminlengden velges av brukeren.
- **Norske krav til bilag.** Foreløpig én regel: et bilag må ha en tekst som beskriver transaksjonen for det kan bokføres. Flere regler legges til her når regnskapsfører har gjennomgått dem.
- **Oppbevaring (5 år).** Kjernen får et nytt manifestelement `retention` (land, kategori, år). Dokumenttjenesten leser reglene fra de installerte appene når et vedlegg knyttes: først landsregel i konfigurasjon, så appregel (den lengste), så standard. `norway` erklærer `NO / bilag / 5 år`. Kjernens konfigurerte standard for `bilag` (5 år) står uendret som reserve og er en driftsinnstilling, ikke norsk logikk i kjernen. Manifestskjemaet i SDK-et fikk også `schedules` (fra ADR-0025), som manglet.
- **Avinstallasjon.** Å tømme en apps data tømmer nå objektene i en rekkefølge som tåler fremmednøkler mellom dem (en tabell som fortsatt er referert venter til de andre er tømt).

## Utsatt

- Innførsel og omvendt avgiftsplikt (SAF-T-kodene 14, 15, 51, 81–92): krever to MVA-linjer per grunnlag (utgående og inngående).
- MVA-meldingens postnummer (RF-0002) og innsending: kommer med Skatteetaten-steget.
- Bilag som flytter utgående og inngående MVA til oppgjørskonto (2740) ved terminslutt.
- Satser før 2022, særregler (kompensasjon, justering av inngående MVA), MVA-kode per konto som forslag.
- Hele NS 4102 og flere norske bilagsregler (når regnskapsfører har gått gjennom).

## Konsekvenser

- Utvidelsesfeltene `norway.vatCode`, `norway.vatSourceLine` og `norway.standardAccount` ligger som kolonner på `finance`-tabellene. Avinstalleres `norway`, blir kolonnene liggende som foreldreløse til fristen (ADR-0013), og dataene i uforanderlige bilag røres ikke.
- Appen kjører som brukeren: den som lagrer en bilagslinje med MVA-kode trenger tillatelse til å opprette bilagslinjer. MVA-posteringer lages av hendelsesbehandleren.
- Eksemplene i testene er lærebokeksempler (25 %, 15 % med avrunding, 12 %, eksport, kjøp uten fradrag, kreditnota). **Skal kontrolleres av regnskapsfører.**
