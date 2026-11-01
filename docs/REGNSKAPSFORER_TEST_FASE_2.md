# Testopplegg for regnskapsfører – fase 2

For deg som er autorisert regnskapsfører (B2) og skal vurdere om bokføringen i systemet er riktig. Du trenger ikke kunne programmere. Dokumentet beskriver hva systemet gjør, med eksempler du kan regne etter, og lister det vi vet er usikkert. Alt står som forslag: ingenting her er avgjort som riktig før du har sagt det.

Vi trenger fra deg: **en vurdering per punkt (riktig / feil / uenig), og rettelser der det er feil.** Funn rettes av oss, og du får se rettelsen.

## Slik kan du teste

Systemet har ikke et ferdig grensesnitt for hånd-testing av regnskapsappene ennå (de kjører i egne containere). Testene våre bruker eksemplene under, og vi kan kjøre dem for deg og vise resultatet på skjerm eller i en SAF-T-fil. Si fra hvilke eksempler du vil se, og hvilke egne du vil legge til.

## A. Kontoplan og MVA-koder

1. Standard kontoplan (NS 4102) legges inn automatisk: 73 kontoer, med firesifrede standardkontoer og SAF-T-gruppering (grupperingskategori og -kode fra Skatteetatens liste). **Vurder:** Er utvalget fornuftig som utgangspunkt for en liten handelsbedrift? Mangler det kontoer som nesten alle trenger? Er navn og typer (eiendel, gjeld, egenkapital, inntekt, kostnad) riktige? Kontoene kan legges til, endres og fjernes, men en konto som er brukt kan ikke fjernes.
2. MVA-kodene er Skatteetatens standardkoder: 1, 11, 12, 13 (inngående), 3, 31, 32, 33 (utgående), 5, 52, 6 (fritak og utenfor), 0 og 7 (ingen MVA-behandling). Satser: 25, 15, 12 og 11,11 prosent. **Vurder:** Kodene, satsene og hvilken MVA-konto hver bruker (2700 utgående, 2710 inngående).
3. **Ikke bygget, men sjekk om det trengs:** innførsel (kode 81–89), omvendt avgiftsplikt, kompensasjon, tap på krav og justering av kapitalvarer.

## B. Bilag og MVA – regn etter

| Eksempel | Tastet | Forventet resultat |
| --- | --- | --- |
| Salg 1 000 kr med 25 % | Debet 1500 kunde 1 250; kredit 3000 1 000 med kode 3 | Systemet lager selv kredit 2700 på 250. Åpen kundepost på 1 250 |
| Kjøp 800 kr med 25 % fradrag | Debet 6300 800 med kode 1; kredit 2400 leverandør 1 000 | Systemet lager selv debet 2710 på 200. Åpen leverandørpost på 1 000 |
| Avrunding | Linje på 100,10 med 15 % | MVA 15,02 (15,015 rundes opp) |
| Reversering | Motpostering av et bokført bilag | Poster med omvendt fortegn, og MVA-oppgjøret nuller ut |

**Vurder:** Regneregelen for MVA (grunnlag × sats, avrundet til to desimaler, halvparten opp), og at MVA-linjen alltid ligger på samme side som grunnlaget. Er det tilfeller der en MVA-kode ikke skal gi automatisk MVA-linje?

Bokførte bilag kan ikke endres, bare motposteres, og får fortløpende nummer uten hull per år. **Vurder:** Er dette i tråd med bokføringsloven (løpende nummer, sporbarhet)? Er det noe vi må ha med på bilaget som ikke er der (for eksempel leverandørens fakturanummer på hver postering)?

## C. Leverandørfaktura og godkjenning

Gangen: PDF kommer inn → systemet leser den (AI) og foreslår leverandør og kontering → en person retter → godkjenning i en flyt → en person bokfører. Kontering foreslås etter **leverandørens siste bokførte faktura** (konto, dimensjoner, MVA-kode). MVA-koden foreslås ut fra satsen på fakturaen (25 % → kode 1, 15 % → 11, 12 % → 13).

**Vurder:**
- Er «sist brukte kontering» et fornuftig forslag, eller bør det være noe annet?
- Kontrollene før godkjenning: linjene med MVA må summere nøyaktig til totalen, fakturanummeret kan ikke gjenbrukes for samme leverandør, avvikende kontonummer stoppes, og fremmed valuta avvises. Mangler det noe? Er noe for strengt?
- Leverandørkontoen er den aktive reskontrokontoen med lavest kontonummer. Er det riktig regel?
- Foreløpig kan ingen faktura bokføres automatisk. Er det slik dere ønsker det?

## D. Betaling og avstemming

- Forfalte leverandørposter blir en betalingsbunt som godkjennes og frigis til banken (som fil).
- Innbetalinger leses fra kontoutskrift og avstemmes mot åpne kundeposter via KID. Er referanse **og** beløp riktig, bokføres bank mot kundereskontro og posten utlignes automatisk. Ellers havner den i en liste for gjennomgang. Delbetaling utligner bare den delen som er betalt.
- Utbetalinger matches på betalings-id.

**Vurder:** Bokføringen av innbetaling (debet bankens hovedbokskonto, kredit kundereskontro) og utbetaling (omvendt). Hva gjør dere ved delbetaling, overbetaling, gebyrer og valutadifferanser? **Ikke bygget:** gebyr og rente fra banken, overbetaling, én KID for flere fakturaer, realisert og urealisert valutagevinst/-tap.

## E. MVA-oppgjør og MVA-melding

Oppgjør per termin summerer grunnlag og avgift per MVA-kode. Meldingen til Skatteetaten lages i deres format:
- utgående koder med grunnlag, sats og avgift (positiv), inngående koder med bare avgift (negativ)
- hele kroner, avrundet **per linje**; summen er summen av de avrundede linjene
- kodene 0, 7, 20, 21 og 22 rapporteres ikke
- fristen er den 10. i den andre måneden etter terminen, flyttet til neste virkedag ved helg og helligdager

**Vurder:** Avrunding per linje (kan gi forskjell på noen kroner mot oppgjøret i øre), hvilke koder som rapporteres, og fristen (kjenner vi alle unntak? for eksempel primærnæring).

## F. SAF-T

Eksport per periode med kontoplan, saldoer, bilag og MVA. Den er validert mot Skatteetatens skjema. **Skatteetatens testverktøy er ikke kjørt ennå.** Kobling fra konto til grupperingskategori/-kode er vårt forslag. **Vurder:** at grupperingen stemmer for kontoene i standardplanen.

## G. Det vi ber deg om å bekrefte eller rette

Dette er de konkrete antakelsene som ligger i koden og trenger din vurdering (fra ADR 0024–0032):

1. Kontoplanens utvalg og SAF-T-grupperingen (A.1).
2. MVA-kodenes satser og kontoer (A.2).
3. MVA-avrundingen (B).
4. Kontering som forslag fra forrige faktura (C).
5. Leverandørkonto = laveste aktive reskontrokonto (C).
6. Bokføring av bankinnbetaling og -utbetaling (D).
7. Avrunding til hele kroner per linje i MVA-meldingen (E).
8. At bilag får fortløpende nummer per år, og at bokførte poster er uforanderlige (B).

## H. Det som mangler og som du bør si fra om hvis det betyr noe

Fremmed valuta på leverandørfakturaer, kreditnota som EHF, omvendt avgiftsplikt og innførsels-MVA, kompensasjon, periodisering/avsetninger, avskrivninger og anleggsmidler, lønn, og årsoppgjør. Fase 2 er den første versjonen av regnskapet, ikke et fullverdig system.
