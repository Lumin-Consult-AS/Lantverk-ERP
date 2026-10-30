# ADR-0012: Dokumentmottak, innboks, filer og objektlager

- **Status:** Vedtatt
- **Dato:** 2026-10-30
- **Sak:** LUM-13 (Steg 9)

## Kontekst

Innkommende dokumenter (fakturaer, bilag, avtaler) skal havne i systemet, kunne knyttes til objekter, og kunne behandles av apper (fakturatolken i fase 2). Dette krever fillager, innkommende e-post, virusskanning, dedupering og oppbevaring. Kravet er EU-drift, både for innkommende og utgående e-post og for lagring.

## Beslutning

### Leverandører (valgt av Jens Nymoen, 2026-10-30)
1. **E-post: Brevo** (fransk). Postmark (eid av ActiveCampaign, lagrer i USA) og Mailgun (amerikansk juridisk enhet under Sinch; en EU-region flytter data, ikke jurisdiksjon) ble valgt bort. Utgående e-post går som SMTP (Brevo støtter SMTP-relé); innkommende via Brevos *inbound parsing* til webhook.
2. **Objektlager: Scaleway Object Storage** (fransk, S3-kompatibelt, Object Lock). Lokalt brukes en MinIO-kompatibel server.
3. Begge er bak **kontrakter med adaptere** (arkitekturregel for integrasjoner): `InboundEmail` (nøytral kontrakt) med `BrevoInboundAdapter`, og `IObjectStorage` med `S3ObjectStorage`. Å bytte leverandør er en ny adapter.
4. Før «EU-drift» lovfestes overfor kunder må Brevos databehandleravtale og underleverandørliste leses; hjelpesider er ikke kontrakter.

### Filer (`core.file`)
5. Innholdet ligger i objektlageret (`tenants/{tenant}/files/{id}`), metadata i `core.file` med SHA-256, størrelse, type og skannestatus. **Filer er uforanderlige**: en databasetrigger avviser endring av innhold, navn, hash, nøkkel og selskap; ny versjon er en ny fil. Skannestatus og oppbevaring er det eneste som kan endres.
6. **Dedupering** per selskap på SHA-256 (unik delvis indeks). Samme innhold lagres én gang; kalleren får den eksisterende filen og beskjed om duplikat (`duplicate: true`; på innboksposter `duplicateFileIds`).
7. **Virusskanning før tilgjengeliggjøring** via ClamAV (`clamd INSTREAM` over TCP). Smittet innhold skrives **aldri** til objektlageret; bare funnet lagres. Er skanneren utilgjengelig, lagres filen som `pending` og er ikke tilgjengelig (**fail closed**; uten konfigurert skanner blir ingenting tilgjengelig). En bakgrunnsarbeider (`FileScanWorker`) skanner ventende filer på tvers av tenanter via `core.claim_pending_scans` (samme `SECURITY DEFINER`-mønster som ADR-0009) og frigir innboksposter som ventet på dem.
8. Maksimal filstørrelse 25 MB (clamd-grensen).

### Vedlegg og oppbevaring (`core.attachment`)
9. Et vedlegg kobler en fil til en hvilken som helst post med en **kategori**. Å knytte krever `read` på posten. Kategorier kan ha **oppbevaringstid per land** (`Documents:Retention:<land>:<kategori>`, standard `bilag` = 5 år; landet er selskapets land, ellers tenantens standardland).
10. Ved oppbevaring settes filen i **Object Lock, compliance-modus** til fristen er ute, slik at ikke engang administratorer kan slette eller korte ned. Lås settes *før* vedlegget opprettes, så oppbevaring uten håndhevelse i lageret aldri oppstår stille. Å fjerne et vedlegg eller en fil innenfor fristen gir `409`. Object Lock kan bare slås på når bøtten opprettes; i drift opprettes bøtten av infrastrukturkode, lokalt og i tester av `CreateBucketIfMissing`.

### Innboks (`core.inbox`, `core.inboxitem`)
11. Én innboks per selskap og kanal (`email`, `upload`, `api`). E-postinnbokser får en **unik adresse** (`inbox-<id>@<Documents:InboxDomain>`, unik på tvers av tenanter). Brevo kaller `POST /api/v1/inbound/brevo?token=…`; hemmeligheten i URL-en er autorisasjonen (webhooken har ingen bruker), og endepunktet er avslått uten konfigurert token.
12. Adressen slås opp uten tenant-kontekst med `SECURITY DEFINER`-funksjonen `core.resolve_inbox`, deretter behandles meldingen under tenantens RLS. Ukjent mottaker gir `200` uten post (ingen evig reprøving); feil gir `5xx` slik at Brevo leverer på nytt. Poster er **idempotente på melding-ID**.
13. En post går `scanning` → `new` → `claimed` → `processed` / `rejected`. `core.inbox.received@1` publiseres i samme transaksjon som posten blir `new`, altså først etter virusskanning. En post med smittet vedlegg avvises med begrunnelse og publiserer ingen hendelse.
14. **Apper claimer poster** atomisk (kun én vinner; `409`/`Aborted` for resten) via `DocumentService` over gRPC (`ClaimInboxItem`, `CompleteInboxItem`, `RejectInboxItem`, `ReleaseInboxItem`, `GetDocument`); den som claimet fullfører. Poster ingen tar er den **manuelle arbeidslisten** (klientside `/inbox`) og telles på startsiden («Nye dokumenter i innboksen», ADR-0010). Apper handler med brukerens tillatelser (ADR-0011).
15. Manuell behandling: «knytt til post» legger alle postens filer ved en post (f.eks. en part) og markerer posten behandlet.

### PDF og utsending
16. HTML-mal + data blir PDF via en **egen renderer-container** (Playwright/Chromium headless, `erp-core/tools/pdf-renderer`). Malen er upålitelig: JavaScript er av og all nettverkstrafikk blokkert. `{{felt}}`-verdier HTML-kodes alltid. PDF-en lagres som `core.file` (kilde `generated`, ikke skannet) og kan sendes som vedlegg på e-post (`POST /api/v1/documents/render`).

### Tillatelser
17. Innebygde tillatelsessett eies av kjernen: `core.documents.reader` og `core.documents.user` (objektene `core.file`, `core.inbox`, `core.inboxitem`, `core.attachment`). De kobles til roller som andre sett (ADR-0011).

## Avgrensninger
- **Brevos webhook-format** er implementert etter dokumentasjonen og testet mot en gjenskapt payload, ikke mot en levende Brevo-konto. Verifiser mot første ekte e-post.
- **S3-adapteren** er testet mot en ekte S3-server bare når `ERP_TEST_S3_IMAGE` peker på et MinIO-kompatibelt bilde (`minio/minio` finnes ikke lenger på Docker Hub); ellers hoppes disse testene over og appen testes mot en lagring i minnet med samme låseregler. Adapteren er ikke kjørt mot Scaleway.
- **PDF-rendereren** er skrevet men ikke bygget eller kjørt (Playwright-bildet er ikke hentet); klienten og malen er testet mot en fake.
- Ikke bygget: lagringsklasser/arkiv, kryptering i hvile utover leverandørens, opplasting i klienten, AI-tolkning, EHF/PEPPOL, OCR (utsatt i planen).
- Oppbevaringsregler leses fra konfigurasjon, ikke fra tenantens data; redigering i klienten kan komme senere.

## Konsekvenser
- **Fordeler:** EU-drift og låsing av bilag håndheves i lageret, ikke bare i appen; apper får et stabilt dokument- og innboks-API; leverandørene kan byttes.
- **Ulemper:** flere bevegelige deler (ClamAV, renderer, objektlager) som må være oppe; filer uten skanner blir stående utilgjengelige til skanneren er tilbake; Object Lock krever at bøtten er riktig opprettet i drift.
