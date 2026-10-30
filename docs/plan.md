# ERP-plattform fra bunn av – plan

Sist oppdatert 30. oktober 2026 · Jens Nymoen

## Premisser

Planen bygger kun på det som er gitt: et nytt ERP fra bunn av, med en grunnplattform og moduler (salg, innkjøp, regnskap osv.) som installerbare apper. Alt utover det er merket som enten **anbefaling** (vanlig praksis i plattform-ERP som Business Central, Odoo, ERPNext) eller **åpent valg** (se siste seksjon).

| Gitt | Anbefaling (kan endres) | Åpent valg |
| --- | --- | --- |
| Helt nytt system, fra bunn | Multi-tenant SaaS som standard driftsform | Målmarked og målkunde |
| Grunnplattform + apper | Apper kan utvide hverandre, ikke bare kjernen | Teknologistack |
| Eksempler på apper: salg, innkjøp, regnskap | Regnskap som obligatorisk «første app» alle andre bygger på | Forretningsmodell og lisens |
|  | Norge/Europa som første regulatoriske marked | Team og budsjett |

De fleste av de åpne valgene er siden besluttet – se neste seksjon.

## Beslutninger

| Beslutning | Hva som er bestemt | Konsekvens for planen |
| --- | --- | --- |
| Posisjonering | En norsk versjon av Odoo: dypere funksjoner per modul, og kunden velger selv hvor mye som brukes og betales for | Prising per app må inn i kjernen fra start (lisens- og abonnementstjeneste). Hver app må fungere alene og i kombinasjon, så avhengighetsgrafen i manifestet er ikke valgfri |
| Målkunde | Handelsbedrifter som driver kjøp og salg, med støtte for både B2B og B2C | Lager er ikke valgfritt i fase 3 – det er kjernen i en handelsbedrift. B2C trekker inn kasse og nettbutikk; B2B trekker inn prislister, kundeavtaler, kreditt og EHF. Produksjon, prosjekt og lønn kan utsettes |
| Pilotkunde | Finnes ikke ennå | Må finnes før fase 3 starter, helst før fase 2 er ferdig, slik at regnskap og handel bygges mot reelle behov. Kravet er en bedrift som tåler å være først |
| Kapasitet | 20 timer i uken | Omtrent en halv stilling. Fasene har grove estimater (se faseplanen); AI-assistert utvikling er forutsatt. Omfanget per fase holdes strengt: alt som ikke trengs for porten utsettes |
| Driftsmodell | Kun multi-tenant | Ingen on-premise eller single-tenant. Tenant-ID på alle rader med rad-nivå-sikkerhet i PostgreSQL; egen database per tenant trengs ikke. Sandkassen for tredjepartskode må være streng, siden all kode kjører i egen drift |
| Lisens | Lukket kjerne, åpent SDK | Kjernen og egne apper er lukket kildekode. SDK, manifestformat, API- og kanalkontrakter og eksempelapper publiseres under permissiv lisens (MIT eller Apache 2.0). Vilkår for partnerapper (eierskap, lisenskrav, inntektsdeling i app-butikken) skrives av advokat før første partner. Kjernen kan åpnes senere; motsatt vei er ikke mulig |
| Geografi | Norge først, men klargjort for flere land – særlig Sverige og Danmark | Land er en førsteklasses dimensjon i kjernen fra start: MVA-koder, kontoplan, rapporteringsformater (SAF-T, svensk SIE, dansk e-faktura/NemHandel), e-fakturaformat og valuta per selskap. Norsk lokalisering er selv en app («Norge-pakken»), slik at Sverige og Danmark blir tilsvarende pakker senere |
| Salgskanaler | Ingen spesifikke kanaler utover kasse i dag. Plattformen skal kunne få egen nettbutikk senere, på samme måte som egen POS | Salgsappen definerer en kanalkontrakt (ordre inn, lager og pris ut, betaling, retur, frakt); eksterne nettbutikker (f.eks. Shopify) blir adaptere, og egen nettbutikk blir senere en adapter mot samme kontrakt. POS- og nettbutikk-kontrakten deles der de overlapper |
| Kasse/POS | Kasse integreres via et POS-rammeverk, slik at flere kassesystemer kan kobles til uten ny integrasjon hver gang. Egen POS utvikles senere | POS-rammeverket er en kontrakt i salgsappen: kontantsalg, retur, betalingsmidler, dagsoppgjør, vare- og prissynk. Hvert kassesystem blir en adapter-app. Kassasystemloven treffer ikke plattformen før egen POS bygges, men dagsoppgjør og kontantsalg må likevel bokføres korrekt |
| Utvidelsesmodell | Tredjeparter skal kunne kjøre egne utvidelser av moduler og legge til egne funksjoner | Tredjepartskode krever sandkasse med ressursgrenser; i praksis egne tjenester per app (senere ev. WebAssembly) |
| Eierskap | Privat prosjekt. Avklaring med Knowit er uproblematisk så lenge ingenting selges før det er avklart | Alt bygges uten kjerneteam og budsjett som forutsetning. Ingenting selges, og ingen betalt pilot inngås, før avklaringen med Knowit er gjort; dette er portkriterium før fase 3. Utvikling, demo-tenant og en ubetalt pilot kan gjøres før |
| Teknologistack | C#/.NET, PostgreSQL, Linux-hosting i EU, apper som egne prosesser, deklarativt UI | Klientteknologi (Blazor eller React) kan utsettes til fase 1 fordi UI er deklarativt |

**Fakturatolk (23. oktober):** fakturatolk med AI, bilagsforslag og konfigurerbar godkjenningsflyt med flere godkjennere er et krav til regnskapsappen. Se egen seksjon. Flytmotoren ligger i fase 1, tolk og bilag i fase 2.

**Bankintegrasjon (24. oktober):** Bank løses med samme mønster som POS og nettbutikk: regnskapsappen definerer en bankkontrakt (`SendPayments`, `GetStatements`, `GetIncomingPayments`, `ApprovePayment`) med ISO 20022 (pain.001, camt.053/054) som internt format. Første adapter er **Aritma**, som dekker de fleste nordiske banker og brukes av bl.a. Fiken og Unimicro; andre adapter er ren ISO 20022 filimport/-eksport, slik at kunder har en vei uten tredjepartskostnad og plattformen ikke er låst til leverandøren. Avstemming, KID-matching og betalingsgodkjenning (via flytmotoren) bygges i regnskapsappen oppå kontrakten. Egne direkte bankintegrasjoner og PSD2-konsesjon er bevisst valgt bort. Før fase 2: avklar partnervilkår, pris per kunde/transaksjon og API-tilgang med Aritma.

**Første POS-adaptere: Sitoo og Visionpoint.** Sitoo har et offentlig dokumentert REST-API med endepunkter for ordrer, kassaapparater, betalingstyper, prislister, lager og Z-rapporter – det dekker omtrent det POS-kontrakten må inneholde, og er et godt første mål for å forme kontrakten. For Visionpoint er det ikke funnet offentlig API-dokumentasjon; integrasjonsmulighetene må avklares med leverandøren før kontrakten låses, slik at den ikke designes rundt bare ett system.

**Ærlig vurdering av «norsk Odoo»:** Odoo har over 20 års utvikling, flere tusen apper og egne lokaliseringspartnere i Norge. Det som kan vinnes på er ikke bredde, men (1) norsk regnskap og lovkrav som er korrekte uten tredjepartsmoduler, (2) et renere app-rammeverk bygget inn fra start, og (3) EU-hosting som standard. Fremdrift bør derfor måles på hvor godt fase 2 fungerer, ikke på antall apper.

## Posisjonering og konkurransebilde

**Posisjonering:** handels-ERP for norske bedrifter som har vokst ut av regnskapssystemet, uten å måtte kjøpe et implementeringsprosjekt.

**Markedet, grovt**

| Segment | Systemer | Sterkt på | Svakt for en handelsbedrift |
| --- | --- | --- | --- |
| Enkelt regnskap, 1–15 ansatte | Fiken, Tripletex, PowerOffice Go, Conta | Billig, selvbetjent, bank og bilag fungerer, Altinn ferdig | Lite eller ingen lagerstyring, ingen innkjøpsflyt, ingen kasse, svak B2B-prising; nettbutikk og POS limes på med tredjepartsintegrasjoner |
| Mellomsegment, 15–150 ansatte | Business Central, Visma Business NXT, Visma.net, Uni Economy, Xledger, 24SevenOffice | Full funksjonalitet, dimensjoner, lager, økosystem | Krever partner og implementeringsprosjekt; hver tilpasning koster konsulenttimer; lisens per bruker uansett bruk. Xledger er økonomi-først |
| Stort | D365 F&O, SAP, IFS | Alt | Irrelevant for målkunden |
| Internasjonalt, app-basert | Odoo | Bredde, app-modell, pris | Norsk lokalisering er partnerlaget og ujevn (SAF-T, EHF, bank, Altinn); lite norsk partnermiljø |

**Gapet:** en handelsbedrift med 10–80 ansatte, fysisk butikk og/eller nettbutikk og B2B-salg, har i dag to valg: bli i Tripletex/PowerOffice med fem integrasjoner ingen eier helheten i (Shopify, Sitoo/Front Systems, lager, Klarna, frakt), eller gjøre et BC-prosjekt med partner. Det første er skjørt, det andre dyrt og tregt.

**Hva plattformen skal være kjent for**

1. Selvbetjent oppstart og betaling per app: start med regnskap og bank som i Tripletex, slå på lager, innkjøp, POS-adapter og nettbutikk-adapter ved behov.
2. Ett system for kjøp, salg, lager og kasse, der Sitoo og Shopify er førsteklasses deler via POS- og kanalkontraktene – og senere egen POS og nettbutikk (unified commerce, som ingen norsk aktør i segmentet tilbyr).
3. Bilagsflyt med AI-tolk og bank innebygd, ikke som tillegg med egen lisens. Den mest synlige forskjellen for regnskapsføreren i hverdagen.
4. Supply chain utover det segmentet får i dag: varer underveis, landed cost, mobilt lager, frakt og tilgjengelighet på tvers av kanaler innebygd; prognose, toll, sporbarhet og leverandørportal som apper.
5. Norge som app, ikke hardkoding, slik at Sverige og Danmark er samme produkt senere.

**Hva som ikke er et fortrinn i Norge:** EU-hosting. Visma, Tripletex og PowerOffice er allerede nordiske; argumentet treffer Microsoft og Odoo, ikke de norske. Ingen bytter ERP for arkitekturen; de bytter fordi regnskapsføreren sier ja og hverdagen blir enklere.

**Salgskanal:** regnskapsførere er sannsynligvis viktigste kanal, slik de er for Tripletex og Fiken. Regnskapsførerens egen hverdag (bilag, bank, MVA, SAF-T) må derfor være sømløs før noe annet.

**Profil på første kunde:** en handelsbedrift som i dag kjører Tripletex eller PowerOffice pluss Shopify pluss Sitoo, og er frustrert over at tallene ikke stemmer på tvers.

## Differensierende funksjoner

Tjue funksjoner som skal skille plattformen fra konkurrentene i segmentet. Noen finnes hos store systemer eller som tillegg, men ingen i målsegmentet har dem innebygd. Fase angir når funksjonen tidligst kan leveres.

| # | Funksjon | Hva det er | Fase |
| --- | --- | --- | --- |
|  | **Regnskap og økonomi** |  |  |
| 1 | Løpende månedsavslutning | Viser hele tiden hva som mangler for å lukke perioden (uavstemte bankposter, bilag i flyt, periodiseringer) | 2 |
| 2 | Automatiske periodiseringer fra bilag | Fakturatolken foreslår periodisering når en faktura gjelder flere måneder | 2 |
| 3 | Likviditetsprognose fra faktiske data | 13 uker frem fra åpne poster, innkjøpsordre og historisk betalingsatferd per kunde | Etter 2 |
| 4 | MVA-kontroll før innsending | Avviksanalyse mot forrige termin og fakturagrunnlag, med forklaring | 2 |
| 5 | Regnskapsførerportal for flere klienter | Én innlogging, status på avstemming, bilag og frister for alle klienter | 2 |
|  | **Salg og kunde** |  |  |
| 6 | Kredittvurdering i ordreflyten | Oppslag ved ny B2B-kunde, automatisk kredittgrense, sperre eller godkjenningsflyt | 3 |
| 7 | Dekningsbidrag i sanntid på ordrelinjen | Med landed cost, frakt og kanalgebyr, før ordren bekreftes | 3 |
| 8 | B2B-kundeportal | Kunden ser egne priser, ordrer, leveranser og fakturaer og bestiller på nytt; bygger på kanalkontrakten | Egen app etter 3 |
| 9 | Kampanjer og priser på tvers av kanaler | Én kampanje definert én gang, gjelder i butikk, nett og B2B med regler per kanal | 3 |
| 10 | Abonnement og gjentakende salg | Fakturering av abonnement, serviceavtaler og faste leveranser | Egen app etter 3 |
|  | **Innkjøp** |  |  |
| 11 | Automatisk treveis matching med toleranser | Innkjøpsordre, mottak og tolket faktura matches; bare avvik går til godkjenning | 3 |
| 12 | Prisavviksvarsel | Tolken oppdager avvik fra avtalt pris og lager avvik eller kreditnotakrav | 3 |
|  | **Kasse og kanaler** |  |  |
| 13 | Kunde på tvers av kanaler | Samme kundekort og kjøpshistorikk fra butikk, nett og B2B | 3 |
| 14 | Retur til hvilken som helst kanal | Kjøpt på nett, returnert i butikk (og omvendt) med riktig lager, regnskap og refusjon | 3–4 |
|  | **Plattform og bruk** |  |  |
| 15 | AI-assistent med tilgang til egne data | Spørsmål om egne tall besvares med lenker til transaksjonene, innenfor brukerens tillatelser | Etter 3 |
| 16 | Unntaksstyrt startside | Viser det som krever handling nå: godkjenninger, lav beholdning, forsinkede forsendelser, frister, avvik | 2 (vokser med hver app) |
| 17 | Konfigurer selv uten utvikler | Egne felt, enkle regler og flyter i UI, på samme deklarative modell som partnerne bruker | 1 (grunnlag), UI i 2 |
| 18 | Selvbetjent migrering fra Tripletex, PowerOffice og Fiken | Kontoplan, saldoer, kunder, leverandører og åpne poster via deres API-er | Før pilot (2–3) |
| 19 | Prøv med egne data | Import av egne data i en testtenant før kunden forplikter seg | Før pilot (2–3) |
| 20 | Søkbar revisjonslogg for brukeren | «Hvem endret prisen, og når?» besvares direkte i kortet | 1 (data), UI i 2 |

Konsekvenser: nr. 17 og 20 bygger på metadata- og auditlaget i fase 1 og krever bare UI senere. Nr. 18 og 19 er salgsforutsetninger og bør være klare før første pilot. Nr. 16 er designet som et utvidelsespunkt (`attention`) der hver app bidrar med egne unntak.

## Supply chain

Målkunden trenger mer supply chain enn Tripletex/PowerOffice har og enn standard Business Central gir uten tillegg. Hver linje er noe som i dag krever et tillegg (Tasklet, nShift, Continia, Inventory Planner) eller ikke finnes i segmentet.

| Område | Standard i dag | Hva plattformen skal tilby | Når |
| --- | --- | --- | --- |
| Varer underveis | Innkjøpsordre, så mottak | Forsendelse som eget objekt mellom ordre og mottak: ETA, sporing, status (bestilt, bekreftet, sendt, i toll, ankommet), dokumenter, og «lager underveis» som teller i tilgjengelighet | Fase 3 |
| Landed cost | Varekost = innkjøpspris | Frakt, toll og speditørfaktura fordeles automatisk på varene i forsendelsen (vekt, volum, verdi), med AI-tolken som leser speditørfakturaen; riktig dekningsbidrag per vare | Fase 3 |
| Mobilt lager | WMS som tillegg eller fraværende | Mottak, plassering, plukk, pakk og telling på mobil fra første versjon | Fase 3 |
| Frakt | Egen integrasjon per transportør | Fraktkontrakt med nShift og Bring som første adaptere: fraktforespørsel, etikett, sporing, vekt/volum fra plukk | Fase 3 |
| Tilgjengelighet på tvers av kanaler | Hver kanal har sitt bilde | Én «available to promise» per vare og lokasjon med reservasjoner fra B2B, nett, butikk og varer underveis, regler per kanal; grunnlag for click & collect, send fra butikk og retur på tvers | Fase 3 |
| Innkjøpsplanlegging | Bestillingspunkt per vare | Etterspørselsprognose per vare og kanal (POS, nett, B2B, sesong), leveringstid og MOQ, innkjøpsforslag gruppert per leverandør med fraktkonsolidering | Egen app etter fase 3 |
| Leverandørportal | Ingenting | Leverandør bekrefter ordre, oppdaterer ETA, laster opp dokumenter og prislister selv | Egen app |
| Toll og import | Utenfor ERP, hos speditør | HS-kode, opprinnelse og tollsats per vare; tolldeklarasjonsgrunnlag fra forsendelsen; speditør/tollrepresentant som adapter mot fraktkontrakten | Egen app |
| Sporbarhet og kvalitet | Lot/serie kun i BC | Lot, serienummer, utløpsdato; kvalitetskontroll ved mottak med avvik og retur til leverandør; tilbakekallingsrapport | Egen app |
| Leverandøroppfølging | Excel | Leveringspresisjon (OTIF), prisavvik ordre/faktura, kvalitetsavvik og reklamasjoner samlet på leverandørkortet | Egen app |
| 3PL og dropshipping | Skreddersøm | 3PL-kontrakt (ordre ut, lagerstatus inn, mottak bekreftet) med adaptere; dropshipping der leverandør sender direkte og ordren følges likevel | Egen app |

**Warehouse management** – to nivåer, begge innebygd. I segmentet har Tripletex og PowerOffice ingen lagerstyring utover beholdning, og Business Central-kunder med reelt lagervolum kjøper nesten alltid Tasklet eller tilsvarende.

| Nivå | Innhold | Når |
| --- | --- | --- |
| Lager (del av handel) | Lagre og hyllelokasjoner, mottak mot forsendelse, plasseringsforslag, plukk, pakk, utsending, varetelling og syklustelling, GS1-strekkode, alt på håndterminal fra start | Fase 3 |
| WMS (egen app, betales separat) | Styrt plassering og slotting etter omløpshastighet (ABC), bølge-, batch- og soneplukk, optimal plukkrute, kartonisering, etterfylling fra reservelager til plukkplass, krysslasting, returmottak med kontroll, oppgavekø med sammenfletting, produktivitet per medarbeider, rampebooking, overføring mellom lagre | Egen app etter fase 3 |

Håndterminaler (Android, f.eks. Zebra og Honeywell) får en egen skanner-klient med frakoblet modus; kjernen har API for frakoblet synk fra fase 1 (se teknisk design).

**Designkonsekvens:** forsendelse, lokasjon og reservasjon må inn i lagerappens datamodell fra start, selv om prognose, portal og toll kommer som apper senere. Alle integrasjoner mot transportører, speditører og 3PL går via kontrakter, slik bank, POS og nettbutikk gjør.

## Målbilde og designprinsipper

Målbildet er en plattform som alene ikke er et ERP, men som blir det når apper installeres. Kjernen eier identitet, data, regler og utvidelsespunkter; appene eier forretningslogikk.

1. **Kjernen vet ingenting om salg eller regnskap.** Den tilbyr datamodell, hendelser, sikkerhet, UI-rammeverk og installasjon. Alt domenespesifikt ligger i apper.
2. **Apper er førsteklasses borgere, også egne.** Salg, innkjøp og regnskap bygges med nøyaktig samme SDK og API som en tredjepart ville brukt. Det er den eneste måten å sikre at utvidelsesmodellen faktisk holder.
3. **Apper utvider apper.** En app må kunne legge til felter, sider, hendelseslyttere og regler i en annen apps objekter (f.eks. en Shopify-app som utvider salgsordre).
4. **Hendelsesdrevet, ikke tett koblet.** Apper snakker via publiserte hendelser og definerte kontrakter, aldri ved å kalle hverandres interne kode.
5. **Alt er versjonert og migrerbart.** Datamodell, API og apper har semantisk versjonering; oppgradering av en app skal aldri kreve nedetid for tenanten.
6. **Revisjonsspor fra dag én.** Bokføringsloven og revisjonskrav gjør at sporbarhet på alle posteringer og masterdata-endringer ikke kan ettermonteres.
7. **API-først.** Alt brukergrensesnittet kan gjøre, kan et API gjøre. UI er en klient av API-et.
8. **Multi-tenant fra start.**

Det som ikke skal gjenskapes fra Business Central: ett monolittisk datalag der tabeller fra ulike moduler refererer hverandre direkte, og en utvidelsesmodell som kom til etter at kjernen var bygget.

## Kjerneplattformen

Kjernen består av åtte domene-uavhengige tjenester, et app-rammeverk over dem og drift under. Ingen av dem vet hva en faktura er.

*Arkitektur i fire lag, ovenfra: apper (salg, innkjøp, regnskap, lager, partnerapper – samme SDK for egne og eksterne) → app-rammeverk (manifest, SDK, utvidelsespunkter, hendelseskontrakter, installasjon) → kjernetjenester → drift (multi-tenant, EU-hosting, database, køer, objektlager, observabilitet).* Appene kaller aldri hverandre direkte; alt går gjennom rammeverket og kjernetjenestene.

| Kjernetjeneste | Ansvar | Hvorfor i kjernen, ikke i en app |
| --- | --- | --- |
| Identitet, tilgang | Brukere, roller, tillatelser på objekt- og feltnivå, SSO (Entra ID m.fl.), tenant-isolasjon | Alle apper trenger samme sikkerhetsmodell |
| Metadata og lagring | Objektdefinisjoner (tabeller, felt, relasjoner) som apper registrerer, skjemamigrering, feltutvidelse av andres objekter | Gjør at én app kan legge felt på en annens objekt uten kodeendring der |
| Hendelsesbuss | Publisering og abonnement på forretningshendelser, garantert levering, replay | Løs kobling mellom apper |
| Flyt og regelmotor | Godkjenningsflyter, statusmaskiner, valideringsregler, nummerserier | Samme godkjenningsflyt for innkjøpsordre og leverandørfaktura |
| Rapportering og søk | Spørring på tvers av apper, dimensjoner, lagrede visninger, eksport | Finansrapportering trenger data fra alle apper |
| API og integrasjon | REST for alle objekter, webhooks, BI-tilgang, bulk-import | API-først-prinsippet |
| Dokumenter og print | Maler, PDF, e-post, EHF/PEPPOL-utsending, arkiv | Alle apper produserer dokumenter |
| Revisjonsspor | Uforanderlig logg av alle endringer og posteringer, hvem/når/hva | Lovkrav og kan ikke ettermonteres |

## App-modellen

En app er en versjonert pakke med et manifest som deklarerer hva den tilfører og hva den avhenger av. Plattformen installerer, oppgraderer og avinstallerer den per tenant uten nedetid.

**Hva et manifest deklarerer**

| Del | Innhold | Eksempel fra en salgsapp |
| --- | --- | --- |
| Identitet | Navn, versjon (semver), utgiver, lisens | `sales` 1.4.0 |
| Avhengigheter | Andre apper med versjonsintervall | `core.finance >= 2.0`, `core.parties >= 1.0` |
| Objekter | Nye tabeller og felt den eier | Salgsordre, Salgslinje, Prisliste |
| Utvidelser | Felt, sider og regler den legger på andres objekter | Legger `Betalingsbetingelse` på Kunde (eid av parter-appen) |
| Hendelser | Hendelser den publiserer og abonnerer på | Publiserer `SalesOrder.Posted`; lytter på `Payment.Received` |
| Sider og navigasjon | UI-definisjoner (deklarative, ikke kode) | Ordreliste, ordrekort, menypunkt under Salg |
| Tillatelsessett | Roller den introduserer | Selger, Salgssjef |
| Rapporter/dokumenter | Maler og datakilder | Ordrebekreftelse, Salgsstatistikk |
| Migrasjoner | Oppgraderingsskript mellom versjoner | 1.3 → 1.4 legger til felt X |
| Sandkasseavtale | Hvilke eksterne URL-er og ressurser den trenger | `api.shopify.com` |

**Utvidelsespunkter kjernen må tilby**

1. Feltutvidelse: legge felt på andres objekter, inkl. på linjenivå.
2. Sideutvidelse: legge til felt, faner, handlinger og faktabokser i andres sider.
3. Hendelseslyttere: før/etter validering, før/etter bokføring, ved statusendring.
4. Regel- og valideringshooks: avvise eller endre en transaksjon før den lagres.
5. Rapportutvidelse: legge kolonner og datakilder til eksisterende rapporter.
6. Grensesnitt/kontrakter: en app definerer et grensesnitt (f.eks. «Betalingsleverandør»), andre apper implementerer det.

**Kjøremodell for app-kode** (besluttet 23. oktober 2026): deklarativ logikk (felt, sider, enkle regler) tolkes av kjernen; kode som trenger mer kjører som egen prosess/container per app og kalles av kjernen via definerte kontrakter. Valgt fordi .NET ikke tilbyr en sikker sandkasse i prosessen. WebAssembly kan erstatte prosessene senere uten at app-kontraktene endres.

**Hvordan apper unngår å låse hverandre**

- En app eier sine objekter; andre kan bare utvide, aldri endre eller slette eide felt.
- Alle kryssreferanser går via stabile objekt-ID-er registrert i metadatatjenesten, ikke via databasetabellnavn.
- Brytende endringer krever ny major-versjon, og plattformen nekter å oppgradere en tenant der avhengige apper ikke støtter den nye versjonen.
- Avinstallasjon er reversibel: appens data arkiveres, ikke slettes, i en definert periode.

## Felles datamodell og masterdata

Noen objekter er så grunnleggende at nesten alle apper trenger dem. De ligger i små «grunnapper» som alltid er installert, ikke i selve kjernen – da kan de likevel versjoneres og utvides som alt annet.

| Grunnapp | Objekter | Brukes av |
| --- | --- | --- |
| Parter | Part (felles for kunde, leverandør, kontakt, ansatt), adresser, kontaktpersoner, bankkontoer | Salg, innkjøp, regnskap, lønn |
| Varer | Vare, enhet, varegruppe, varianter, strekkoder | Salg, innkjøp, lager, produksjon |
| Dimensjoner | Frie analysedimensjoner (avdeling, prosjekt, kostnadsbærer) | Regnskap og alle transaksjonsapper |
| Valuta og land | Valutaer, kurser, land, MVA-koder per land | Alle |
| Organisasjon | Selskap (juridisk enhet), lokasjoner, regnskapsår, perioder | Alle; grunnlag for InterCompany |
| Kalender og tid | Arbeidsdager, helligdager per land | Logistikk, lønn, prosjekt |

Designvalg:

- **Én part, flere roller.** Kunde og leverandør er roller på samme part, ikke separate tabeller. Det fjerner duplikater og gjør netting mellom kunde- og leverandørreskontro naturlig.
- **Selskap er en dimensjon på data, ikke en egen database.** Multi-selskap innenfor én tenant gjør InterCompany og konsolidering til en spørring, ikke en integrasjon.
- **Transaksjoner er uforanderlige etter bokføring.** Korreksjon skjer ved motpostering, aldri ved redigering.
- **Dokumenter har en felles livssyklus.** Kladd → godkjent → bokført → arkivert gjelder for ordre, faktura, kreditnota, journal osv., og eies av kjernens statusmaskin.
- **Alle beløp lagres i både transaksjonsvaluta og selskapets bokføringsvaluta** med kursen som ble brukt.

## Teknologivalg

Stacken ble valgt 23. oktober 2026.

| Lag | Valgt | Begrunnelse | Motargument som ble akseptert |
| --- | --- | --- | --- |
| Backend-språk og SDK | C#/.NET | Utbredt, robust, langtidsstøtte; BC-partnere i Norge kan det allerede; svært godt egnet for AI-assistert utvikling | Mer boilerplate enn TypeScript; lavere terskel for webutviklere går tapt |
| Database | PostgreSQL | Moden, JSONB for metadata, rad-nivå-sikkerhet for tenant-isolasjon, ingen lisenskostnad | Mindre kjent i BC-miljøet enn SQL Server |
| Hosting | Linux/Kubernetes i EU-region (Hetzner, OVH, Azure EU m.fl.) | .NET kjører likt på Linux; ingen binding til Azure | Må drifte selv eller kjøpe managed Kubernetes |
| Tredjepartskode | Egne prosesser/containere per app; WebAssembly (Wasmtime) som senere optimalisering | AssemblyLoadContext er ingen sikkerhetsgrense i .NET; prosessisolasjon er enkelt og sikkert | Høyere ressursbruk per app og mer latens enn kode i prosessen |
| UI | Deklarative sider i manifestet; klient velges separat (Blazor hvis én person bygger, React hvis webutviklere skal bidra) | Appene blir uavhengige av klientteknologi | Mindre UI-frihet for app-utviklere |
| API | REST + OpenAPI; GraphQL eller OData for lesing vurderes senere | Bredest støtte hos integratorer og BI-verktøy | GraphQL utsettes |
| Hendelsesbuss | Outbox i PostgreSQL + enkel kø først | Transaksjonssikkert og enkelt å drifte for én person | Må byttes til NATS/Kafka ved høyt volum |

Anbefalinger uavhengig av valg: ett språk for både kjerne og app-SDK; databasen skal kunne byttes, men flere databaser støttes ikke i praksis.

## Norske og europeiske krav

Regnskapsappen er den eneste appen som ikke kan være «enkel først»: den må oppfylle lovkrav fra første kunde. Listen må verifiseres mot Skatteetaten og Regnskap Norge før design låses.

| Krav | Hva det betyr for plattformen | Hvor det hører hjemme |
| --- | --- | --- |
| Bokføringsloven og bokføringsforskriften | Sporbarhet, dokumentasjon av bilag, oppbevaring (normalt 5 år), ingen redigering av bokførte poster | Revisjonsspor i kjernen, regnskapsapp |
| SAF-T Regnskap | Eksport av hovedbok, reskontro og kontoplan i Skatteetatens XML-format på forespørsel | Regnskapsapp, standard kontoplan-mapping |
| MVA-melding | Standard MVA-koder (SAF-T-koder), innsending via API | Regnskapsapp (Norge-pakken) |
| EHF / PEPPOL BIS Billing 3.0 | Sende og motta e-faktura, krav for salg til offentlig sektor; aksesspunkt må velges | Dokumenttjeneste i kjernen |
| A-melding | Månedlig rapportering ved lønn | Kun hvis lønnsapp bygges; ellers integrasjon |
| Årsregnskap og skattemelding | Næringsspesifikasjon, Altinn | Årsoppgjør-app (se under) |
| GDPR | Behandlingsgrunnlag, sletting/anonymisering av personer, databehandleravtale, data i EU/EØS | Kjernen: parter-appen må støtte anonymisering |
| Kommende: obligatorisk e-faktura og digital bokføring B2B | Foreslått i Norge og EU (ViDA); tidslinje ikke endelig, verifiser | Dokumenttjeneste, regnskapsapp |

Regnskap, dokumenttjeneste (EHF) og revisjonsspor må være ferdige og testet før noe selges til norske kunder. Salg og innkjøp kan lanseres i enklere versjoner.

**Direkte innsending til myndighetene (30. oktober 2026)** – et krav, ikke valgfritt. Alt skjer via offentlige API-er for sluttbrukersystemer.

| Innsending | Kanal | Når |
| --- | --- | --- |
| MVA-melding med validering før innsending | Skatteetatens mva-API via Altinn 3, Maskinporten og systembruker | Fase 2 (Norge-pakken) |
| Frister (mva og skattemelding) | Skatteetatens Frister-API | Fase 2 – vises i startside og regnskapsførerportal |
| Krav og betalinger (forskuddsskatt, restskatt, mva) | Skatteetatens API | Fase 2 – automatisk avstemming mot hovedbok |
| Skattemelding med næringsspesifikasjon (erstatter næringsoppgaven) | Skatteetatens innleverings-API, komplett XML | Egen app «Årsoppgjør» etter fase 2 |
| Årsregnskap til Regnskapsregisteret | Altinn App-API, Maskinporten og systembruker | Egen app «Årsoppgjør» etter fase 2 |

Årsoppgjør-appen (skattemessige forskjeller, saldoavskrivninger, skattekostnad, noter, signering) må være ferdig før pilotkundens første fulle regnskapsår er over. Tilgang til API-ene søkes hos Skatteetaten og krever sannsynligvis et registrert foretak som systemleverandør – avklares sammen med Knowit.

## Fakturatolk og bilagsflyt

Krav: innkommende fakturaer sendes til en AI-tolk som lager et bilagsforslag; bilaget må godkjennes, og kan sendes på en flyt med flere godkjennere der rekkefølgen og reglene for flyten defineres av kunden. Dette tilsvarer rollen Continia Document Capture har i Business Central, men innebygd fra start.

| Del | Hvor | Begrunnelse |
| --- | --- | --- |
| Mottak av dokumenter (e-postinnboks, opplasting, EHF/PEPPOL-mottak, skanning) | Dokumenttjenesten i kjernen | Alle apper kan ta imot dokumenter |
| AI-tolk (PDF/bilde → leverandør, dato, beløp, MVA, linjer, KID) | Egen app «Fakturatolk» | Kan byttes ut uten å røre regnskapet; EHF går utenom tolken siden den er strukturert |
| Bilagsforslag med konteringsforslag (konto, dimensjoner, MVA-kode) | Regnskapsappen, med læring fra tidligere godkjente bilag per leverandør | Konteringen er regnskapslogikk |
| Godkjenningsflyt (sekvensiell eller parallell, beløpsgrenser, stedfortreder, eskalering, kommentarer) | Flyt- og regelmotoren i kjernen | Samme motor brukes for innkjøpsordre, utlegg og andre dokumenter |
| Flytdefinisjon som kunden selv setter opp | Deklarativ konfigurasjon i kjernen, ikke kode | Kunden skal kunne endre flyten uten utvikler |
| Bokføring etter siste godkjenning, med originaldokumentet lenket til posteringen | Regnskapsappen | Bokføringslovens krav til dokumentasjon av bilag |

Krav som følger av dette:

- AI-tolken må kjøre i EU, og fakturaer med personopplysninger må ikke sendes ut av EU/EØS. Valg av AI-leverandør er åpent.
- Tolken foreslår, mennesket godkjenner. Ingen bilag bokføres automatisk uten fullført flyt; fullautomatisk bokføring for betrodde leverandører kan bli en regel senere.
- Alle endringer i bilagsforslaget før godkjenning logges (hva tolken foreslo, hva som ble rettet).
- Godkjenning må kunne gjøres fra mobil og fra e-post.

## Faseplan

Rekkefølgen er gitt av avhengighetene: ingen app kan bygges før rammeverket finnes, og ingen kunde kan bruke systemet før regnskapet er lovlig. Estimatene gjelder 20 timer i uken med AI-assistert utvikling, er grove og bør revideres etter de første ukene i fase 1.

| Fase | Estimat ved 20 t/uke | Usikkerhet |
| --- | --- | --- |
| 0 Beslutninger | Noen uker | Lav – det meste er avgjort |
| 1 Kjerne | 6–12 måneder | Høy – metadata- og utvidelsesmodellen er det vanskeligste i hele prosjektet |
| 2 Regnskap | 6–9 måneder | Middels – kjent domene, men SAF-T, EHF, bank og Norge-pakken må være korrekte |
| 3 Handel | 9–12 måneder | Middels – mye volum; mobilt lager, forsendelser og tilgjengelighet på tvers av kanaler utgjør økningen fra 6–9 |
| 4 Økosystem | Løpende | Avhenger av partnere |

Samlet er det realistisk å regne med to til to og et halvt år til første pilotkunde kjører daglig drift.

*Rekkefølge med porter: 0 Beslutninger → (arkitektur låst) → 1 Kjerne → (demo-app kjører) → 2 Regnskap → (SAF-T/EHF testet) → 3 Handel → (pilotkunde live) → 4 Økosystem → 5 Senere.* Hver port er et stoppunkt der neste fase ikke starter før kriteriet er oppfylt.

| Fase | Leveranse | Portkriterium |
| --- | --- | --- |
| 0 Beslutninger | Arkitekturbeslutninger (ADR-er) for stack, kjøremodell, tenant-modell, målkunde | Alle åpne spørsmål besvart og dokumentert |
| 1 Kjerne | Metadata, identitet, hendelsesbuss, app-rammeverk, generisk UI-klient, land og selskap som grunnbegreper, flyt- og regelmotor, dokumentmottak, API for frakoblet synk | En demo-app kan installeres, utvide en annen app og avinstalleres uten kodeendring i kjernen; en flyt med to godkjennere kan defineres og kjøres; endringer gjort frakoblet synkes med konfliktløsning |
| 2 Regnskap | Grunnapper (parter, varer, dimensjoner, valuta, organisasjon), landsuavhengig regnskapsapp, Norge-pakken med hovedbok-oppsett, MVA, SAF-T, EHF, fakturatolk med bilagsforslag og godkjenningsflyt, bankkontrakt med Aritma-adapter og ISO 20022-filadapter, bankavstemming og KID-matching | SAF-T-eksport validert mot Skatteetatens testverktøy; EHF sendt og mottatt via aksesspunkt; regnskapsfører har testet bokføring; en skannet leverandørfaktura går fra innboks via tolk og flyt til bokført bilag og betalt via bank; innbetaling med KID avstemmes automatisk; ingen norsk logikk ligger utenfor Norge-pakken |
| 3 Handel | Salgsordre til faktura (B2B: prislister, kundeavtaler, kreditt; B2C: kontantsalg, retur), innkjøpsordre med forsendelser og varer underveis, landed cost, leverandørfaktura med matching mot tolkede fakturaer, lager med lokasjoner og hyllelokasjoner, beholdning, varekost, reservasjoner og tilgjengelighet på tvers av kanaler, skanner-klient for håndterminal (mottak, plassering, plukk, pakk, telling, frakoblet), fraktkontrakt med nShift/Bring, POS- og kanalkontrakt (uten adaptere) | Avklaring med Knowit er gjort; én handelsbedrift kjører daglig kjøp, salg, lager og utsending på systemet, med lagerarbeid på håndterminal |
| 4 Økosystem | Offentlig SDK, dokumentasjon, app-butikk, første partnerapper: Sitoo-adapter, Shopify-adapter | En app laget utenfor kjerneteamet er publisert og installert hos en kunde |
| 5 Senere | Egen POS og egen nettbutikk som adaptere; WMS-app; supply chain-apper (innkjøpsplanlegging, leverandørportal, toll/import, sporbarhet, leverandøroppfølging, 3PL); Årsoppgjør-app; Sverige- og Danmark-pakker | Produkterklæring etter kassasystemloven; første kunde i Sverige eller Danmark |

Fase 1 er den største risikoen: det er fristende å begynne på regnskap før rammeverket er ferdig, og da ender regnskap som en del av kjernen – akkurat det som skal unngås.

## Team, kompetanse og arbeidsmåte

Rollene må dekkes uansett størrelse; i et lite team dekker én person flere.

| Rolle | Hvorfor den trengs | Kan dekkes av |
| --- | --- | --- |
| Produkteier med ERP-domenekunnskap | Prioritere riktig, kjenne hva kunder faktisk gjør daglig | Jens, gitt BC-bakgrunnen |
| Plattformarkitekt | Metadata, utvidelsesmodell, multi-tenant | Erfaren backend-arkitekt |
| Backend-utviklere (kjerne) | Kjerne og rammeverk | 2+ for redundans |
| App-utviklere | Bygger grunnapper og regnskap med SDK-et, og avslører hull i det | Kan være samme personer, men bruker kun SDK-et |
| Frontend-utvikler | Generisk UI-klient | 1 |
| Autorisert regnskapsfører / revisor | Verifisere at regnskap, MVA og SAF-T er korrekt | Ekstern rådgiver fra fase 2 |
| DevOps/sikkerhet | Multi-tenant drift, EU-hosting, pentest | Deltid tidlig, heltid før pilot |

Arbeidsmåte:

- **Arkitekturbeslutninger skrives som ADR-er** fra dag én.
- **Dogfooding av SDK-et:** ingen snarveier i kjernen for egne apper. Hvis regnskapsappen trenger noe SDK-et ikke har, utvides SDK-et.
- **Én demo-tenant som alltid fungerer**, oppdatert ved hver sammenslåing.
- **AI-assistert utvikling** er realistisk for appene (mye repeterende CRUD- og posteringslogikk), mindre for kjernen der arkitekturvalgene er avgjørende.

## Risikoer

| Risiko | Konsekvens | Mottiltak |
| --- | --- | --- |
| Regnskap sniker seg inn i kjernen | Utvidelsesmodellen blir fiksjon | Port 1: demo-app må virke før regnskap startes; egne apper bruker kun SDK |
| Utvidelsesmodellen er for svak | Partnere må forke; økosystem oppstår ikke | Kopier de utvidelsespunktene som faktisk brukes i BC og Odoo |
| Utvidelsesmodellen er for sterk | Tredjepartskode kan ta ned tenanten | Sandkasse, ressursgrenser per app, deklarativt der det går |
| Regnskapet er ikke korrekt | Kunder får problemer med Skatteetaten | Regnskapsfører fra fase 2; test mot SAF-T-validator |
| Omfangsvekst | Aldri ferdig | Pilotkunde med avgrenset behov definerer «minimum»; alt annet er apper senere |
| Konkurranse fra etablerte (BC, Tripletex, Fiken, Xledger, PowerOffice) | Vanskelig å vinne kunder på funksjonalitet alene | Tydelig posisjonering (se egen seksjon) |
| Avhengighet av nøkkelpersoner | Prosjektet stopper hvis én faller fra | ADR-er, dokumentasjon |
| Migrering fra eksisterende systemer | Kunder bytter ikke uten god import | Bulk-import i kjernen tidlig; selvbetjent migrering fra Tripletex, PowerOffice og Fiken |

## Åpne spørsmål

**Formål og marked**

- [x] Hvem er målkunden? *Handelsbedrifter som driver kjøp og salg, både B2B og B2C.*
- [x] Hva er grunnen til at dette skal finnes? *Dypere funksjoner per modul, kunden velger og betaler for det den bruker.*
- [x] Kommersielt, Knowit-produkt, privat eller åpen kildekode? *Privat prosjekt med lukket kjerne og åpent SDK.*
- [x] Er Norge første marked? *Norge først, klargjort for Sverige og Danmark.*
- [ ] Henger dette sammen med planen om en europeisk Office-utfordrer, eller er det uavhengig?
- [x] Kasse/POS: bygges eller integreres? *POS-rammeverk først; egen POS senere.*
- [x] Hvilke kassesystemer skal første adapter lages for? *Sitoo og Visionpoint.*
- [x] Salgskanaler utover kasse? *Ingen konkrete nå; egen nettbutikk senere via kanalkontrakt.*
- [ ] Hvordan integrerer Visionpoint (API, filutveksling, annet)? Må avklares med leverandøren.

**Teknologi og arkitektur**

- [x] Språk for kjerne og app-SDK? *C#/.NET på PostgreSQL.*
- [x] Skal tredjeparter kunne kjøre egen kode? *Ja, som egne prosesser per app.*
- [x] Multi-tenant, single-tenant eller on-premise? *Kun multi-tenant.*
- [x] Skal UI være helt generisk? *Deklarative sider i manifestet; klient (Blazor eller React) velges i fase 1.*
- [x] Bank? *Bankkontrakt med Aritma som første adapter og ISO 20022-filer som andre.*
- [ ] Hvilke andre eksterne systemer må støttes fra dag én? EHF-aksesspunkt og AI-leverandør i EU er ikke valgt.

**Omfang og ressurser**

- [ ] Hvilke apper må finnes før første kunde? *Delvis: lager er nødvendig; prosjekt og lønn utsettes.*
- [x] Hvem skal bygge det, og hvor mye tid er tilgjengelig? *Jens, 20 timer i uken.*
- [x] Finnes det en konkret pilotkunde? *Nei, må finnes før fase 3.*
- [ ] Hvordan skal appene prises og distribueres? *Delvis: kunden betaler for det den bruker; inntektsdeling for partnerapper er ikke avgjort.*

**Juridisk**

- [ ] Hvem er regnskapsfører/revisor som verifiserer lovkravene i fase 2?
- [x] Kolliderer dette med arbeidsavtalen hos Knowit? *Uproblematisk så lenge ingenting selges før det er avklart. Portkriterium før fase 3.*
- [ ] Produktnavn og varemerkesjekk (LAFT.io finnes allerede i norsk programvare; «Laftverk» og «Kai» er kandidater som må sjekkes hos Patentstyret og EUIPO).
