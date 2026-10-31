# Implementeringsplan – fase 2: regnskap

**Status: utkast til godkjenning.** Ingen Linear-saker er opprettet ennå; de opprettes når planen er godkjent og beslutningene under «Beslutninger først» er tatt. Planen følger samme format som `docs/IMPLEMENTATION_PLAN.md` (fase 1) og bygger på `docs/plan.md` (faseplan, norske krav, fakturatolk).

Fase 2 bygger grunnappene, en landsuavhengig regnskapsapp og Norge-pakken, slik at en skannet leverandørfaktura kan gå hele veien fra innboks til betalt bilag. Alt bygges som apper mot det offentlige SDK-et (regel 2 i `AGENTS.md`): oppdager vi at SDK-et mangler noe, utvides SDK-et.

**Porten for fase 2** (fra `docs/plan.md`):

1. SAF-T-eksport validert mot Skatteetatens testverktøy.
2. EHF sendt og mottatt via aksesspunkt.
3. En regnskapsfører har testet bokføringen.
4. En skannet leverandørfaktura går fra innboks via tolk og flyt til bokført bilag og betalt via bank.
5. En innbetaling med KID avstemmes automatisk.
6. Ingen norsk logikk ligger utenfor Norge-pakken.

Estimat i planen: 6–9 måneder ved 20 timer i uken. Stegene tas i rekkefølge; hvert steg har mål, innhold, «Ferdig når» og eventuelt «Beslutning først».

---

## Beslutninger først (før de stegene som er markert)

Disse må avgjøres av deg før respektive steg starter. Kodeagenten stopper og spør (se `AGENTS.md`).

| # | Beslutning | Trengs før | Anbefaling |
| --- | --- | --- | --- |
| B1 | Navn og ID-er: regnskapsappen (`finance`?) og Norge-pakken (`norway`?) | Steg 3 | **Avgjort 31. oktober 2026:** `finance` og `norway`. ADR-0018 |
| B2 | Hvem er regnskapsfører/revisor som verifiserer lovkravene? (åpent spørsmål i planen) | Steg 3 (design av hovedbok) | Finn tidlig; kravet er en autorisert regnskapsfører som kan teste |
| B3 | Aggregering: skal data-API-et få summering/gruppering (kjernen), eller beregner regnskapsappen selv? | Steg 0 | **Avgjort 31. oktober 2026:** generell aggregering i data-API-et (sum, count, gruppering). ADR-0016 |
| B4 | Lisens og prising per app (planen sier at det må inn i kjernen) | Steg 0 eller før pilot | **Avgjort 31. oktober 2026:** utsettes til før første pilot. ADR-0017 |
| B5 | Aritma: partnervilkår, pris og API-tilgang | Steg 6 (Aritma-adapter) | Avklar nå; filadapteren (ISO 20022) bygges uavhengig. **Status 31. oktober 2026:** dialog pågår, møte med Aritma mandag |
| B6 | EHF-aksesspunkt (PEPPOL) | Steg 9 | Velg leverandør med EU-drift og sandkasse. **Status 31. oktober 2026:** søknad om sandkasse sendt til Storecove (kandidat, ikke endelig valg); sammenlign med minst ett norsk aksesspunkt før valg |
| B7 | AI-leverandør i EU til fakturatolk | Steg 8 | Velg leverandør som lover EU-behandling uten trening på kundedata |
| B8 | Skatteetaten/Altinn: systemleverandørstatus, Maskinporten og systembruker. Avklaring med Knowit er en forutsetning | Steg 10 (innsending) | Søk tidlig, siden saksbehandling tar tid. Generering og validering bygges uten |
| B9 | Pilotkunde (helst før fase 2 er ferdig) | Før fase 3 | Begynn å lete nå |

---

## Steg 0 – Kjerne-forutsetninger for regnskap

**Mål:** Fylle de hullene i kjernen som regnskap ikke kan bygges uten. Kjernen skal fortsatt ikke kjenne regnskapsbegreper.

**Innhold**
- **Nummerserier** (kjerne, generisk): navngitt serie per selskap og år, med valg om hullfri. Brukes til bilagsnummer, fakturanummer osv. Allokering skjer i samme transaksjon som objektet opprettes, slik at en tilbakerullet transaksjon ikke etterlater hull i en hullfri serie.
- **Uforanderlige objekter**: et objekt kan erklæres uforanderlig etter en gitt status (bokført). Databasen avviser UPDATE og DELETE på slike rader (trigger), ikke bare API-et. Korreksjon skjer med motpostering (regel 5).
- **Aggregering i data-API-et** (se B3): sum, antall, min/maks og gruppering med filter, med samme tillatelser og tenant-regler som vanlig spørring.
- **Bruker- og rolleadministrasjon**: API og klientside for å opprette brukere og gi roller (funnet under demoen: det finnes ikke i dag, og godkjenningsflyt og tillatelser trenger det).
- **Opprydding fra fase 1**: S3-adapteren kjørt mot en ekte Scaleway-bucket; publisering av `erp-samples` ligger fortsatt utsatt (ADR-0015).

**Ferdig når** en testapp kan bruke en hullfri nummerserie, et uforanderlig objekt avvises ved endring også via direkte SQL som applikasjonsrollen, og en aggregeringsspørring gir riktig sum per gruppe for kun egen tenant (tester som beviser at data ikke lekker).

**Beslutninger:** B3 og B4 er avgjort (ADR-0016 og ADR-0017). Lisens og prising bygges ikke i steg 0.

---

## Steg 1 – Grunnapper: Varer og Dimensjoner

**Mål:** De små grunnappene som regnskapet og senere handel bygger på. Parter (`core.parties`) finnes allerede.

**Innhold**
- `core.items` (Varer): vare, enhet, varegruppe, strekkoder. Varianter kan vente til fase 3 (regel 8).
- `core.dimensions` (Dimensjoner): frie analysedimensjoner (avdeling, prosjekt, kostnadsbærer) med verdier og gyldighet, referert fra transaksjoner.
- Alle som deklarative apper (uten kode), med sider, tillatelsessett og i18n (nb og en).

**Ferdig når** begge installeres, brukes fra klienten og kan avinstalleres igjen, og en test-app kan legge til et eget felt på en vare.

---

## Steg 2 – Organisasjon, valuta og perioder

**Mål:** Selskapets rammer for regnskap.

**Innhold**
- Regnskapsår og perioder per selskap med status (åpen, stengt) og stenging per periode.
- Valutakurser per dato (kilde: manuell inntasting og import fra fil; Norges Bank som senere adapter). Beløp lagres i transaksjonsvaluta og selskapets bokføringsvaluta med kursen som ble brukt.
- Land som grunnbegrep brukes allerede; ingen norsk logikk legges her.

**Ferdig når** et selskap har regnskapsår med perioder, en periode kan stenges, og en kurs kan slås opp per dato og valutapar.

---

## Steg 3 – Regnskapsapp (`finance`, landsuavhengig)

**Mål:** Hovedbok som er korrekt uten å vite noe om Norge.

**Innhold**
- Kontoplan (konto, kontotype, sammenslåing), journal og bilag med linjer, posteringer, dimensjoner på linjer, bilagsnummer fra hullfri serie.
- Statusmaskin: kladd → godkjent → bokført; bokførte posteringer er uforanderlige (steg 0). Korreksjon med motpostering. Periodelås håndheves ved bokføring.
- Reskontro for kunde og leverandør (åpne poster, utligning) med part fra `core.parties`.
- Rapporter via aggregering: saldobalanse, hovedbokutskrift, reskontroliste, med eksport.
- Bilag lenkes til originaldokument (vedlegg) som bokføringsloven krever.
- Hendelser som `finance.journal.posted@1`.

**Ferdig når** en regnskapsfører (B2) kan opprette kontoplan, føre bilag, bokføre, få avvist endring i bokført bilag, korrigere med motpost, og kjøre saldobalansen. Tester dekker balanse (debet = kredit), periodelås og at andre tenanter ikke ser data.

**Beslutning først:** B1, B2.

---

## Steg 4 – Norge-pakken: kontoplan og MVA

**Mål:** All norsk logikk i én app (`norway`).

**Innhold**
- Standard kontoplan (NS 4102) og mapping til SAF-T standardkontoer, som data i pakken.
- MVA-koder (SAF-T-koder) med satser og gyldighet, MVA-beregning på bilagslinjer, MVA-oppgjør per termin (grunnlag per kode).
- Regler: norske krav til bilag, oppbevaring (5 år) som oppbevaringsregel i dokumenttjenesten.
- Pakken installeres av tenanten; regnskapsappen kjenner den kun gjennom utvidelsespunkter og hendelser.

**Ferdig når** en norsk tenant får standard kontoplan og MVA-koder ved installasjon, MVA beregnes riktig på salg og kjøp (test mot kjente eksempler fra regnskapsfører), og en søk i `finance` viser at ingen norske verdier ligger utenfor `norway`.

---

## Steg 5 – SAF-T Regnskap

**Mål:** Eksport i Skatteetatens format på forespørsel.

**Innhold**
- Eksport av hovedbok, kontoplan, reskontro og MVA-koder som XML, per periode, som bakgrunnsjobb med nedlasting.
- Validering mot Skatteetatens skjema og testverktøy.

**Ferdig når** en eksport for en testtenant godkjennes av Skatteetatens testverktøy (porten, punkt 1).

---

## Steg 6 – Bankkontrakt og ISO 20022-filadapter

**Mål:** Bank løses med kontrakt og adaptere (planen).

**Innhold**
- Bankkontrakten i `finance`: `SendPayments`, `GetStatements`, `GetIncomingPayments`, `ApprovePayment`.
- Første adapter: ISO 20022-fil (pain.001 ut, camt.053/054 inn) som egen app. Betalingsgodkjenning via flytmotoren.
- Aritma-adapteren er en egen leveranse som venter på B5.

**Ferdig når** en betalingsfil (pain.001) lages fra godkjente leverandørposter og en kontoutskrift (camt.053) leses inn.

**Beslutning først:** B5 (for Aritma-adapteren, ikke for filadapteren).

---

## Steg 7 – Bankavstemming og KID-matching

**Mål:** Innbetalinger avstemmes automatisk.

**Innhold**
- KID-generering og validering; matching av innbetalinger mot åpne kundeposter; forslag for det som ikke matcher, til manuell behandling i arbeidsliste.
- Avstemming av banktransaksjoner mot hovedbok.

**Ferdig når** en innbetaling med KID utligner riktig kundepost uten manuelt arbeid (porten, punkt 5).

---

## Steg 8 – Fakturatolk og bilagsflyt

**Mål:** Skannet leverandørfaktura → bokført bilag → betalt.

**Innhold**
- Egen app «Fakturatolk»: leser dokument fra innboksen, tolker leverandør, dato, beløp, MVA, linjer og KID, og leverer strukturerte data. Kjører i EU (B7).
- Bilagsforslag i `finance` med konteringsforslag (konto, dimensjoner, MVA-kode) som lærer av tidligere godkjente bilag per leverandør. Alle rettelser før godkjenning logges (forslag mot endelig).
- Godkjenningsflyt via flytmotoren (finnes fra fase 1); bokføring først etter fullført flyt, aldri automatisk; originaldokument lenkes til bilaget; betaling via bankkontrakten.
- Godkjenning fra mobil og e-post (finnes fra fase 1).

**Ferdig når** en skannet PDF går fra innboks via tolk og flyt til bokført bilag og betalt via bank (porten, punkt 4).

**Beslutning først:** B7.

---

## Steg 9 – EHF (PEPPOL)

**Mål:** Sende og motta e-faktura.

**Innhold**
- Dokumenttjenesten får EHF/PEPPOL BIS Billing 3.0: motta (går utenom tolken, siden den er strukturert) og sende, via valgt aksesspunkt som adapter.
- Utgående faktura fra `finance` genererer EHF; innkommende EHF blir bilagsforslag.

**Ferdig når** en EHF sendes og mottas via aksesspunktets testmiljø (porten, punkt 2).

**Beslutning først:** B6.

---

## Steg 10 – Skatteetaten: MVA-melding, Frister og Krav og betalinger

**Mål:** Direkte innsending til myndighetene.

**Innhold**
- MVA-melding generert fra MVA-oppgjøret med validering før innsending; innsending via Altinn 3, Maskinporten og systembruker.
- Frister vises på startsiden (attention); krav og betalinger avstemmes mot hovedbok.
- Generering og validering bygges uavhengig av adgang; selve innsendingen venter på B8.

**Ferdig når** en MVA-melding valideres og sendes mot Skatteetatens testmiljø.

**Beslutning først:** B8.

---

## Steg 11 – Port: demonstrasjon og regnskapsførertest

**Mål:** Lukke porten for fase 2.

**Innhold**
- Demoveiledning (som `docs/DEMO_FASE_1.md`) som viser alle seks punkter.
- Regnskapsfører (B2) tester bokføring og MVA mot egne eksempler; funn rettes.
- En sjekk som beviser at ingen norsk logikk ligger utenfor `norway`.

**Ferdig når** alle seks portkriteriene er oppfylt og dokumentert.

---

## Avhengigheter og rekkefølge

```
0 Kjerne ─▶ 1 Grunnapper ─▶ 2 Perioder/valuta ─▶ 3 Regnskap ─▶ 4 Norge/MVA ─▶ 5 SAF-T
                                                       │
                                                       ├─▶ 6 Bankkontrakt ─▶ 7 Avstemming/KID
                                                       └─▶ 8 Fakturatolk ─▶ 9 EHF ─▶ 10 Skatteetaten ─▶ 11 Port
```

Steg 6–7 og 8–9 er uavhengige av hverandre og kan bytte rekkefølge. Eksterne avklaringer (B5–B9) har ledetid, så de bør settes i gang parallelt med steg 0–3.

## Utsatt (skal ikke bygges i fase 2)

Handel (salg, innkjøp, lager), skanner-klient, POS og nettbutikk, Årsoppgjør-app (skattemelding og årsregnskap), lønn og A-melding, Sverige- og Danmark-pakker, varianter på varer, rapportdesigner og dashboards, fullautomatisk bokføring for betrodde leverandører, app-butikk og partnerapper.

## Risikoer

| Risiko | Mottiltak |
| --- | --- |
| Regnskapet er ikke korrekt | Regnskapsfører (B2) tidlig, test mot SAF-T-validator, motpostering og uforanderlighet håndhevet i databasen |
| Tilgang til Skatteetatens API tar tid | Søk tidlig (B8); bygg generering og validering uavhengig |
| Aritma-vilkår blir dårlige | Filadapteren gir en vei uten tredjepart |
| Norsk logikk lekker inn i `finance` | Egen sjekk i steg 11; utvidelsespunkter i stedet for betingelser på land |
| Tolken gir feil som ser riktige ut | Mennesket godkjenner alltid; alle rettelser logges |
| Kapasitet (20 t/uke) | Hold omfanget strengt; flytt alt som ikke trengs for porten til «Utsatt» |
