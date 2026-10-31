# ADR-0024: Regnskapsappen `finance`

Status: akseptert (31. oktober 2026). Skal gjennomgås av regnskapsfører (B2).

## Beslutning

- `finance` er en kodet app (container) mot det offentlige SDK-et. Avhengigheter: `core.parties`, `core.dimensions`, `core.organization`.
- **Kladd og bokført er to sett objekter.** Brukeren jobber i `finance.voucher` og `finance.voucherline`. Ved overgangen `post` kontrollerer appen (OnValidate): minst to linjer, debet eller kredit på hver linje, debet = kredit, kontoene finnes og er aktive, reskontrokontoer har part, fremmed valuta har beløp og kurs, og det finnes en åpen periode for datoen. Deretter lager den (OnAfterSave) i samme transaksjon det bokførte bilaget `finance.journalentry` med hullfritt bilagsnummer per år, posteringene `finance.posting` og åpne poster `finance.openitem` for linjer på reskontrokonto.
- **Bilagsnummeret tildeles først ved bokføring**, ikke når kladden opprettes. Da kan kladder slettes uten å lage hull (ADR-0019), og en bokføring som feiler bruker ingen nummer.
- **Uforanderlig** (ADR-0020): bilag, bilagslinjer, bokførte bilag og posteringer kan ikke endres eller slettes etter bokføring, heller ikke med direkte SQL. Linjene settes til «posted» av appen ved bokføring.
- **Korreksjon med motpostering.** Et kladdebilag med `reverses` = et bokført bilag får linjene til det bokførte bilaget med debet og kredit byttet. Bokføres det, får det nye bilaget `reversalOf`; et bilag kan bare motposteres én gang (unik nøkkel).
- **Reskontro**: `finance.openitem` er foranderlig (åpent beløp). En utligning (`finance.settlement` med `finance.settlementitem`) fullføres ved overgangen `complete`: minst to linjer, samme part, beløpene har samme fortegn som åpen post og er ikke større, og summen er 0. Fullføring reduserer åpne beløp, og poster som når 0 settes til utlignet. Kunde- og leverandørposter kan utlignes mot hverandre (netting).
- **Perioder** opprettes av appen når et regnskapsår opprettes (`core.fiscalyear.created@1`): én per måned.
- **Rapporter**: saldobalanse og kontospesifikasjon er aggregeringer over `finance.posting` (ADR-0016). Egne rapportsider kommer senere.
- Alt landsspesifikt (kontoplan, MVA, SAF-T) ligger i Norge-pakken, ikke her.

## Utsatt

- Realisert og urealisert valutagevinst/-tap (omregning på bilagslinjer og kursoppslag er gjort, se ADR-0025).
- Godkjenningsflyt på bilag (flytmotoren kan kobles til overgangen `post`, og gjøres i fakturatolk-steget).
- Avskrivninger, periodisering, budsjett, konsolidering.
- Rapportsider med eksport (saldobalanse som eget skjermbilde).

## Konsekvenser

- Dimensjoner på linjene er tre faste plasser (`dimension1..3`) som refererer `core.dimensionvalue`. Flere kan legges til som utvidelse.
- Avinstallasjon av `finance` med bokførte data kan ikke slette dem (uforanderlighet): arkivet beholdes til det fjernes manuelt som databaseeier. Det er ønsket virkning for lovpålagt oppbevaring, men må avklares med regnskapsfører (oppbevaringstid).
