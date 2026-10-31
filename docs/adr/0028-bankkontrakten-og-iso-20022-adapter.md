# ADR-0028: Bankkontrakten og ISO 20022-filadapteren

Status: akseptert (31. oktober 2026). Betalingsfil og kontoutskrift er kontrollert mot ISO 20022-skjemaene, men ikke prøvd mot en bank; det skjer med B5 (Aritma) eller en banks testmiljø.

## Beslutning

- **Kontrakten er objekter og hendelser i `finance`, ikke et grensesnitt apper kaller.** Apper kan ikke kalle hverandre, og kjernen har ingen vei fra en app til en annens grensesnitt. Det som finnes (og som resten av plattformen bruker) er data og hendelser, så kontrakten ligger der. De fire operasjonene fra planen er:
  - `ApprovePayment`: betalingsbunten (`finance.paymentbatch`) går gjennom flytmotoren (`submit` → godkjenning av rollen `finance.approver` → `approve`).
  - `SendPayments`: en person frigir den godkjente bunten (`release`), og finance publiserer `finance.paymentbatch.released@1`. Adapteren lager og leverer filen og skriver resultatet tilbake på bunten (`deliveryStatus` sendt/feilet, `file`, `message`).
  - `GetStatements` og `GetIncomingPayments`: adapteren lager `finance.bankstatement` og `finance.banktransaction` (innbetaling positiv, utbetaling negativ), med unik ekstern id slik at samme fil kan leses flere ganger uten duplikater.
  Nye adaptere (Aritma, Visma, en banks API) implementerer den samme kontrakten uten å røre finance.
- **Godkjenning og frigivelse er to handlinger.** Hendelsesbehandlere kjører med rettighetene til den som utløste hendelsen (ADR-0009). Hadde godkjenningen utløst sendingen, måtte godkjenneren ha rett til å endre bunten og lagre filer. Nå kan godkjenner og den som frigir være ulike personer (arbeidsdeling), og adapteren handler med den frigivendes rettigheter.
- **Betalingsbunt.** Når en bunt opprettes med «forfall til og med», lager finance en betaling per forfalt åpen leverandørpost som ikke allerede ligger i en betaling (leverandørens bankkonto hentes fra feltet `finance.bankAccount` på parten, som finance legger til som utvidelse). Bunten kan ikke sendes til godkjenning uten bankkonto, en betalingsdato som ikke er passert, minst én betaling, mottakers konto på hver betaling og bankkontoens valuta. Betalinger kan bare endres mens bunten er et utkast; en post betales av én betaling.
- **Finance vet ikke om land, bank eller filformat.** Bankkonto (`finance.bankaccount`) har kontonummer/IBAN, kontoeier og hovedbokskonto. Betalingsreferansen (KID i Norge) er et generisk felt.
- **Adapteren `bank.iso20022`.** Skriver pain.001.001.03 og sjekker filen mot det innebygde ISO-skjemaet før den lagres (en fil som ikke består, legges aldri ut; bunten får `failed` og en melding). Leser camt.053 (kontoutskrift) og camt.054 (innbetalingsmelding) ut fra strukturen, uavhengig av meldingsversjon. Innlesing skjer ved at man laster opp filen og oppretter en `bank.import` med bankkonto; filen må tilhøre den valgte kontoen, og den samme filen kan leses flere ganger uten å lage dobbelt. Betalingsfilen lastes ned fra bunten og leveres til banken av en person.
- **Referanse.** En strukturert referanse sendes som kreditorreferanse (`KID`); ellers sendes meldingen som ustrukturert tekst.

## Utsatt

- Direkte forbindelse til bank (Aritma m.fl.): egen leveranse når B5 er avklart.
- Statusmeldinger fra banken (pain.002), avviste betalinger og at en betaling blir «betalt» når den vises på kontoutskriften (kommer med avstemmingen i steg 7). En feilet bunt slettes og lages på nytt.
- Utenlandsbetalinger, flere debetsdatoer per fil og betalinger i flere valutaer.
- En post med flere detaljer i kontoutskriften holdes som én transaksjon (første detalj).
- Nedlastingsknapp i klienten for betalingsfilen.

## Konsekvenser

- Rolle → tillatelsessett er tenantens avgjørelse: rollen `finance.approver` må kobles til tillatelsessettet `finance.approver` én gang.
- Kontoutskrifter og betalingsfiler er kontrollert mot ISO-skjemaene i testene; om en bank godtar filen avhenger av bankens egen profil (BIC-krav, referanseformat), som må prøves mot bankens testmiljø.
