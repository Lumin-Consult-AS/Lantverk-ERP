# ADR-0021: Brukeradministrasjon

Status: akseptert (31. oktober 2026)

## Kontekst

Tillatelser og godkjenningsflyt (ADR-0010 og ADR-0011) bruker roller, men det fantes ikke noen måte å opprette brukere eller gi dem roller uten direkte databasetilgang. Det ble tydelig under demoen av fase 1.

## Beslutning

- Tenant-administratorer (`tenant.admin`) får `GET/POST /api/v1/users`, `PUT /users/{id}/roles`, `POST /users/{id}/activate` og `/deactivate`.
- En bruker kobles til en person i innloggingstjenesten (OIDC) via `externalSubject`. Selve kontoen, passordet og SSO-oppsettet administreres ikke her.
- Rollen `core.user` er alltid med. Roller i tokenet leses ved utstedelse, så endringer gjelder fra neste innlogging (høyst token-levetiden).
- Beskyttelse mot utestengelse: den siste aktive administratoren kan hverken miste administratorrollen eller deaktiveres.
- Klienten får siden «Brukere» for samme operasjoner.

## Konsekvenser

- Alle endringer går gjennom vanlig tenant-/RLS-håndheving og revisjonssporet på `core.user`.
- Enkel modell nå (regel 8): ingen invitasjoner, e-postbekreftelse eller gruppesynk fra innloggingstjenesten.
