# AGENTS.md – ERP-plattform

Instruksjoner for AI-kodeagenter (Antigravity, VS Code Copilot, Claude Code, Cursor, Codex). Les denne filen og `docs/IMPLEMENTATION_PLAN.md` før du gjør endringer.

## Prosjektet

Privat prosjekt (Jens Nymoen). Et nytt ERP fra bunn av for norske handelsbedrifter (B2B og B2C): en domene-uavhengig kjerne der alle moduler – regnskap, salg, innkjøp, lager osv. – er installerbare apper. Posisjonering: «norsk Odoo» med korrekt norsk regnskap innebygd, betaling per app, og et app-rammeverk der tredjeparter kan utvide alt.

Bakgrunnsdokumenter:
- `docs/plan.md` – forretningsplan, beslutninger, faseplan
- `docs/teknisk-design-fase-1.md` – fullt teknisk design for fase 1

`docs/IMPLEMENTATION_PLAN.md` er alltid til stede og er det du jobber etter.

## Låste beslutninger

| Område | Beslutning |
| --- | --- |
| Språk og plattform | C#/.NET (nyeste LTS), ASP.NET Core, EF Core, PostgreSQL, Linux-containere på Kubernetes i EU |
| Drift | Kun multi-tenant. `tenant_id` på alle rader, rad-nivå-sikkerhet (RLS) i PostgreSQL |
| Apper | Egen container per app-versjon, gRPC mot kjernen. Apper har aldri direkte databasetilgang. Rent deklarative apper (uten kode) kjøres av kjernen uten container |
| UI | Deklarative sidedefinisjoner (YAML) i appens manifest, rendret av én generisk klient. Egen skanner-klient for håndterminaler i fase 3 |
| Lisens | Lukket kjerne; åpent SDK (Apache 2.0): SDK, manifestformat, proto-kontrakter, eksempelapper |
| Geografi | Norge først. Land er grunnbegrep i kjernen; all norsk logikk ligger i en egen app («Norge-pakken») |
| Integrasjoner | Alltid kontrakt + adaptere: bank (Aritma, ISO 20022-fil), POS (Sitoo, Visionpoint), nettbutikk (Shopify), frakt (nShift, Bring) |

## Arkitekturregler som ikke skal brytes

1. **Kjernen vet ingenting om domenet.** Ingen kode i `erp-core` refererer til ordre, faktura, konto, vare eller lager. Domenelogikk hører hjemme i apper.
2. **Egne apper bygges kun mot det offentlige SDK-et.** Ingen snarveier inn i kjernen for egne apper. Mangler SDK-et noe, utvid SDK-et.
3. **Tillatelser og tenant håndheves i kjernens data-API**, aldri bare i UI eller i appen.
4. **Ingen databasetilgang uten RLS.** Applikasjonsrollen har ikke `BYPASSRLS`; `app.tenant_id` settes per transaksjon.
5. **Bokførte transaksjoner er uforanderlige.** Korreksjon skjer med motpostering. Revisjonsspor skrives av databasetriggere til append-only `_audit`-tabeller.
6. **Alt er versjonert (semver).** Brytende endringer i manifest, hendelser eller API krever ny major og migrasjon.
7. **Hendelser kan aldri avvise en transaksjon.** Validering skjer i synkrone hooks; hendelser leveres asynkront, minst én gang, og handlere må være idempotente.
8. **Velg det enkleste som oppfyller kravet.** Det som er merket «utsatt» i planen skal ikke bygges nå.

## Arbeidsmåte for agenten

- Jobb på **ett steg om gangen** fra `docs/IMPLEMENTATION_PLAN.md`, i rekkefølge. Ikke start et steg før det forrige oppfyller sitt «Ferdig når».
- Hvert steg har et Linear-saksnummer (LUM-x). Bruk det i branch-navn og commit-meldinger.
- Når et steg krever en beslutning som står under «Beslutning først»: stopp og spør. Ikke velg selv.
- Skriv en kort ADR i `docs/adr/` (nummerert, Markdown) for hver arkitekturbeslutning som tas underveis.
- Skriv tester for alt i kjernen. Tenant-isolasjon skal ha egne tester som beviser at data ikke lekker mellom tenanter.
- Hold endringer små og leverbare; hvert steg skal kunne demonstreres i en lokal demo-tenant.

## Konvensjoner (forslag – kan endres)

- Kode, identifikatorer og kodekommentarer på engelsk; dokumentasjon og UI-tekster på norsk (bokmål), med i18n-nøkler.
- Tester: xUnit, med Testcontainers for PostgreSQL.
- Objekt-ID-er på formen `app.objekt` (f.eks. `core.party`, `sales.order`); hendelser på formen `app.objekt.hendelse@versjon`.
- Lokal utvikling via Docker Compose: PostgreSQL, OIDC-leverandør, MinIO (S3), Mailpit.

## Repoer (planlagt)

| Repo | Synlighet | Innhold |
| --- | --- | --- |
| `erp-core` | Privat | Kjerne-API, app-vert, bakgrunnsarbeider, metadata, flyt, dokumenter |
| `erp-client` | Privat | Generisk UI-klient |
| `erp-sdk` | Offentlig | `Erp.Sdk`, proto-kontrakter, manifestskjema, `erp` CLI |
| `erp-apps` | Privat | Egne apper (`core.parties`, senere `finance`, `sales` …) |
| `erp-samples` | Offentlig | Demo-appen `demo.contacts` og mal for ny app |

Pakke- og CLI-navn (`Erp.Sdk`, `erp`) er plassholdere til produktnavnet er bestemt.
