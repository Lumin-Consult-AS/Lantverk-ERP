# ADR-0009: Hendelsesbuss – outbox, levering, webhooks og replay

- **Status:** Vedtatt
- **Dato:** 2026-10-30
- **Sak:** LUM-11 (Steg 7)

## Kontekst

Til og med steg 6 leverte kjernen hendelser (`OnEvent`) direkte til app-containere etter commit, og svelget feil. Det gir ingen garanti om at en hendelse publiseres hvis og bare hvis transaksjonen ble bekreftet, ingen gjentatte forsøk, ingen logg og ingen webhooks. Steg 7 erstatter dette med en transaksjonell outbox.

## Beslutning

1. **Lagringstid: 90 dager** (beslutning tatt av Jens Nymoen, 2026-10-30). Hendelser eldre enn 90 dager slettes av bakgrunnsarbeideren. Lengre lagring er bevisst utsatt.
2. **Outbox i PostgreSQL**, i `core`-skjemaet, alle tabeller med `tenant_id` og RLS (`ENABLE` + `FORCE`, policy `tenant_isolation`):
   - `core.event_log`: én rad per publisert hendelse (navn `app.objekt.hendelse@versjon`, aggregat-ID, tenant, selskap, bruker, korrelasjons-ID, tidspunkt, JSON-nyttelast). Append-only for `erp_app` (kun `SELECT`, `INSERT`).
   - `core.event_delivery`: én rad per abonnent og hendelse (`subscriber_type` = `app` | `webhook`), med status `pending` | `delivered` | `dead`, antall forsøk, neste forsøk og siste feil. Dødbrevkøen er raderne med status `dead`.
   - `core.webhook_subscription`: webhook-URL og hendelsesmønster per tenant.
   - `core.tenant_secret`: tenantens hemmelighet for webhook-signering (opprettes ved behov).
3. **Samme transaksjon.** `event_log`- og `event_delivery`-radene skrives i samme databasetransaksjon som dataendringen som utløste hendelsen. Rulles transaksjonen tilbake, finnes ingen hendelse. Hendelser kan aldri avvise en transaksjon (arkitekturregel 7): leveringsfeil påvirker aldri avsenderen.
4. **Abonnenter bestemmes ved publisering** fra installerte apper (`events.subscribes` i manifestet) og aktive webhooks, slik at en abonnent som legges til senere ikke får gamle hendelser uten eksplisitt replay.
5. **Bakgrunnsarbeider** (`EventDeliveryWorker`) poller leveringskøen og vekkes umiddelbart etter commit:
   - Arbeideren må finne forfalte leveranser på tvers av tenanter uten `BYPASSRLS`. Det gjøres med én minimal `SECURITY DEFINER`-funksjon, `core.claim_due_deliveries(limit, lease)`, som kun returnerer `(delivery_id, tenant_id)` og setter en kort leie (`next_attempt_at`) slik at flere arbeidere ikke tar samme leveranse (`FOR UPDATE SKIP LOCKED`). Dette er samme mønster som `core.resolve_login_tenant` (ADR-0003 punkt 5). Selve leveringen skjer deretter under vanlig tenant-kontekst og RLS.
   - Som for `resolve_login_tenant` forutsetter funksjonene at tabelleier (`erp_migrator`) ikke er underlagt RLS.
   - `core.purge_expired_events(retention)` er en tilsvarende `SECURITY DEFINER`-funksjon, siden `erp_app` ikke har `DELETE` på `event_log`.
6. **Rekkefølge og forsøk.** Levering er minst én gang. Rekkefølgen er garantert per (abonnent, aggregat-ID): en leveranse hentes ikke før tidligere ventende leveranser for samme abonnent og aggregat er levert eller død. Ved feil: eksponentiell backoff (5 s × 2^forsøk, maks 1 time) og status `dead` etter 8 forsøk. En død leveranse blokkerer ikke senere hendelser.
7. **Levering til apper** går via `AppService.DeliverEvent` (gRPC) per handler. Appen må svare `Acknowledged`; ellers regnes forsøket som feilet. Idempotens-hjelperen i SDK-et (hendelses-ID per tenant og handler) gjør at gjentatte leveranser og replay er trygge.
8. **Webhooks** leveres som HTTP `POST` med JSON-kropp og signaturen `X-Erp-Signature: sha256=<hex>` (HMAC-SHA256 over `<tidsstempel>.<kropp>` med tenantens hemmelighet), samt `X-Erp-Timestamp`, `X-Erp-Event-Id` og `X-Erp-Event`. Alle 2xx-svar regnes som levert.
9. **Replay.** `core.event_log` kan leses via API, og en hendelse kan spilles av på nytt til en valgt app eller webhook. Replay oppretter en ny leveranse med samme hendelses-ID (`is_replay = true`), uten rekkefølgegaranti.
10. **Appers `Publish` via gRPC** går gjennom samme outbox.
11. `core.workflow.*` og `core.inbox.received@1` forberedes ved at publiseringsveien er generell; manifestvalideringen tillater allerede abonnement på dem. Selve flyt- og innboksfunksjonene kommer i steg 8–9.

## Konsekvenser

- **Fordeler:** Hendelser publiseres hvis og bare hvis transaksjonen ble bekreftet; leveringsfeil stopper ikke andre abonnenter; full sporbarhet og replay; NATS/Kafka kan senere settes inn bak samme grensesnitt.
- **Ulemper:** Levering er asynkron (kort forsinkelse); handlere må være idempotente; `event_log` vokser og må renses (90 dager).
- **Bevisst utsatt:** eksakt-én-gang-semantikk, globale rekkefølgegarantier, lagring ut over 90 dager, kryptering av tenant-hemmeligheter i hvile (nøkkelhvelv).
