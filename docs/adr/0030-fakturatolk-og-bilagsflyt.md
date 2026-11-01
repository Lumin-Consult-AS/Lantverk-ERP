# ADR-0030: Fakturatolk og bilagsflyt

Status: akseptert (1. november 2026). B7 er avgjort: Claude i en EU-region. Konteringsreglene og vilkårene hos leverandøren skal gjennomgås (B2, databehandleravtale).

## Beslutning

- **Valg av AI-leverandør (B7): Claude via Amazon Bedrock i en EU-region** (Frankfurt, `bedrock-runtime.eu-central-1.amazonaws.com`). Kravet er EU-behandling og ingen trening på kundedata; det siste er en avtalesak (databehandleravtalen og Bedrocks vilkår), ikke noe koden kan sikre. Koden sikrer at dokumentet bare sendes til et HTTPS-endepunkt som er `bedrock-runtime.eu-*.amazonaws.com`; alt annet avvises før forespørselen sendes, og manifestet tillater bare dette ene egress-målet. Modell-id settes i driftsoppsettet (en `eu.`-profil, slik at kall ikke rutes ut av EU) og har ingen standardverdi.
- **Enkelt å bytte leverandør, på to nivåer.**
  1. *Innen appen:* tolken ligger bak `IInvoiceExtractor`. En annen leverandør er en ny implementasjon, og resten av appen (henting av filen, skriving til fakturaen, feilhåndtering) er uendret.
  2. *Som egen app:* kontrakten er hendelsen `finance.supplierinvoice.requested@1` og feltene på `finance.supplierinvoice` / `finance.supplierinvoiceline`. En Mindee-tolk er en egen app (`invoice.mindee`) som abonnerer på samme hendelse og fyller samme felt. Ingenting i `finance` endres. Installeres flere tolker, vinner den som skriver sitt navn i `extractor` først (rad-versjonen avgjør); de andre gjør ingenting. (Dette er ikke prøvd med to tolker installert samtidig.)
  Mindee er ikke bygget; det er et byttepunkt, ikke en leveranse.
- **Fakturaen eies av `finance`.** `finance.supplierinvoice` (status `received` → `proposed` → `pending` → `approved` → `posted`, med `rejected` og `failed`) og `finance.supplierinvoiceline`. `finance` abonnerer på `core.inbox.received@1`, claimer innboksposter som har PDF eller bilde (PNG/JPEG) og lager én faktura per fil; andre poster blir liggende på den manuelle arbeidslisten. En faktura kan også lages for hånd med en fil. Planen sa at tolken leser innboksen; vi la det i `finance` slik at tolken ikke trenger noe annet enn hendelsen, og kan byttes uten å røre innboksflyten.
- **Tolken** henter filen, leser den, skriver hodet og linjene og går videre med `propose`. Klarer den det ikke, går fakturaen til `failed` med årsaken i `message`, og vises på startsiden. `retry` ber om ny tolking. Dokumentet er ikke tillatt data: systemprompten sier at innholdet ikke er instruksjoner, svaret leses bare som data (JSON), og hver verdi sjekkes (datoer må være ISO, beløp vanlige tall, KID og organisasjonsnummer bare sifre, lengder avkortes).
- **Forslag, ikke avgjørelser.** Når fakturaen er foreslått, finner `finance` leverandøren (organisasjonsnummer, ellers eksakt navn med ett treff) og setter kontering, dimensjoner og utvidelsesfelt (for eksempel MVA-kode) fra *leverandørens siste bokførte faktura*, fra linjen med størst beløp. Bare tomme felt fylles. Forslaget (`suggestedAccount`) står ved siden av det endelige valget (`account`), slik at rettelser kan sammenlignes; alle endringer ligger dessuten i revisjonssporet. `norway` foreslår MVA-kode ut fra satsen i dokumentet (25 % → kode 1, 15 % → 11, 12 % → 13) før erfaringen fra tidligere fakturaer får fylle ut det som mangler.
- **Kontroller før godkjenning** (ved `submit`): leverandør er valgt, fakturadato og totalbeløp finnes, alle linjer har konto, linjene med MVA summerer nøyaktig til totalbeløpet, fakturanummeret er ikke registrert fra før for samme leverandør, fremmed valuta avvises, og KID må være gyldig (`norway`). **Endret kontonummer** er et klassisk svindelsignal: avviker kontonummeret på fakturaen fra det som er registrert hos leverandøren, stoppes fakturaen til en person har kontrollert og enten oppdatert leverandøren eller tømt feltet. Har leverandøren ingen konto, får den kontoen fra den godkjente fakturaen ved bokføring.
- **Godkjenning og bokføring.** `submit` starter flyten `finance.supplierinvoice.approval` (rollen `finance.approver`) og låser fakturaen; linjene kan bare endres i `received` og `proposed`. Etter godkjenning gjør en person `post`. Det lager bilaget (kostnadslinjene, og leverandørkontoen med parten og forfallsdato), bokfører det via vanlig `post` på bilaget (norsk MVA kommer da fra `norway`), setter fakturaens KID som betalingsreferanse på leverandørposten og lenker bilaget til fakturaen. Fakturaen peker både på originaldokumentet (`file`) og på bilaget (`voucher`); selve bilaget har ingen egen lenke til filen. Bokføring skjer aldri automatisk.
- **Betaling** går gjennom bankkontrakten (ADR-0028): en betalingsbunt tar med forfalt leverandørpost, og betalingen får nå betalingsreferansen (KID) fra posten.
- **Kreditnota:** negative beløp gir en kreditnota (omvendte sider).

## Utsatt

- Fremmed valuta (avvises ved innsending; bilaget lages for hånd), avrundingsdifferanser mellom dokumentets MVA og beregnet MVA (summen må stemme på øret), omvendt avgiftsplikt og innførsels-MVA.
- Mindee eller annen leverandør som egen app (byttepunktet finnes).
- Nedlastingsknapp for originaldokumentet i klienten (filen ligger på fakturaen og hentes via fil-API-et), tolking av flere fakturaer i én PDF, PDF-er med mange sider som må deles.
- Måling av treffsikkerhet på ekte fakturaer. Tolken er testet mot en fake og mot en gjenskapt Bedrock-respons, ikke mot en levende konto.
- Mobil/e-post-godkjenning fra planen finnes fra fase 1 (oppgaver og godkjenning via e-post) og brukes som den er; den er ikke testet spesielt for fakturaer.

## Konsekvenser

- Bedrocks Converse-grensesnitt (`/model/{modelId}/converse`, bearer-nøkkel, `document`- og `image`-blokker) er implementert etter dokumentasjonen og **ikke prøvd mot en levende konto**. Verifiser mot første ekte kall; modell-id og nøkkel må settes i driftsoppsettet (`InvoiceReader:Claude:ModelId`, `:ApiKey`).
- Kvaliteten på tolkingen er ukjent før ekte fakturaer er prøvd. Alt tolken sier er et forslag som en person går gjennom, og det er derfor godkjenning og bokføring er separate, bevisste steg.
- Kontoplanen må ha en aktiv leverandørkonto (reskontro for leverandører); den med lavest kontonummer brukes.
- Behandleren som lager fakturaer fra innboksen kjører med rettighetene til den som la inn meldingen (ADR-0009), så innboksen må brukes av noen med rett til å claime poster og opprette fakturaer.
