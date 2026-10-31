# ADR-0029: Bankavstemming og KID

Status: akseptert (31. oktober 2026). Bokføringsreglene (hvilke kontoer som brukes, delbetaling, avrunding) skal gjennomgås av regnskapsfører (B2).

## Beslutning

- **KID ligger i `norway`, avstemmingen i `finance`.** KID (2–25 siffer, kontrollsiffer MOD10 eller MOD11) er norsk. Avstemmingen bruker bare et generelt felt, `finance.openitem.paymentReference`, og er uavhengig av land og referanseformat. Den som kjenner formatet fyller feltet.
- **KID-generering.** Når en kundepost (positiv post på en reskontrokonto for kunder) opprettes, lager `norway` en KID: et hullfritt løpenummer (`norway.kid`, nummerserie med 9 sifre) med MOD10-kontrollsiffer, og setter den som betalingsreferanse på posten. Leverandørposter får ingen. KID-en er unik per post. En betalingsreferanse på en betaling til leverandør (`finance.payment`) må være en gyldig KID (MOD10 eller MOD11).
- **Matching.** Når adapteren har lagt inn en banktransaksjon, leter `finance` etter en åpen post (hendelsen `finance.banktransaction.created@1`):
  - innbetaling: åpen kundepost med samme betalingsreferanse; ligger referansen på flere poster, avgjøres det ikke;
  - utbetaling: ende-til-ende-id fra banken peker på en betaling i en betalingsbunt (adapteren sender betalingens id), og den peker på leverandørposten;
  - uten referanse: finnes nøyaktig én åpen post med samme beløp, er det et forslag, aldri en automatisk avstemming.
- **Automatisk bare når alt stemmer.** Referanse **og** beløp må stemme med åpent beløp. Da godkjennes transaksjonen straks. Alt annet (annet beløp, delbetaling, flere treff, ingen treff) går til gjennomgang (`review`) med et forslag der det finnes, og med forklaring i `note`. Ubehandlede transaksjoner vises på startsiden som «Banktransaksjoner til gjennomgang».
- **Godkjenne = bokføre og utligne i ett steg.** Overgangen `accept` lager et bilag (innbetaling: debet bankens hovedbokskonto, kredit kundekontoen med parten; utbetaling omvendt), bokfører det, og utligner den åpne posten mot den nye posten i en utligning, alt i samme transaksjon. En feil gjør at transaksjonen blir stående i gjennomgang med årsaken. Et mindre beløp enn åpent beløp utligner delvis; et større avvises. En person kan godkjenne et forslag eller velge post selv (`suggestedOpenItem`), eller ignorere transaksjonen.
- **Avstemming mot hovedboken.** `finance.bankreconciliation` er et øyeblikksbilde per dato: bankens saldo (siste utskrift til og med datoen), saldoen på bankkontoens hovedbokskonto, differansen og det som ennå venter på gjennomgang. Tallene settes av appen når posten opprettes.
- **SDK utvidet:** `IAppDataClient.TransitionAsync` (gRPC `Transition`) lar en app utføre en statusovergang (med hooks, tillatelse og flyt) som den påloggede brukeren. Matching og bokføring bruker den.
- **Rettigheter.** Hendelsesbehandleren kjører med rettighetene til den som la inn filen (ADR-0028), så den må være bokholder med rett til å bokføre og utligne.

## Utsatt

- Gebyrer og rente i bankutskriften, samlebetalinger med flere detaljer, og valutaforskjeller (utligning med differanse).
- Matching på motpartens kontonummer eller navn, og læring fra tidligere valg.
- Overbetaling (beløp større enn åpent beløp) og betaling av flere fakturaer med én KID.
- KID med lengde eller kontrollsiffer etter kundens ønske (vi bruker 10 sifre MOD10), KID på faktura-PDF (kommer med utgående faktura).

## Konsekvenser

- Poster som er bokført før `norway` ble installert, har ingen KID.
- Gjennomgangslisten er det ordinære stedet for alt avstemmingen ikke kan avgjøre; ingenting bokføres uten at referanse og beløp stemmer eller en person har godkjent.
