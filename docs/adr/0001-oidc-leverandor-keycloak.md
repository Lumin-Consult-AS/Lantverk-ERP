# ADR 0001: Keycloak som OIDC-leverandør og intern token-utveksling

- **Status:** Vedtatt
- **Dato:** 2026-10-30
- **Sak:** LUM-5

## Kontekst

Kjernen i ERP-plattformen skal ikke selv være en identitetsleverandør med egen passordhåndtering, men støtte OpenID Connect (OIDC) med mulighet for både innebygd brukerdatabase for mindre kunder og federasjon mot Entra ID eller Google Workspace. I tillegg skal apper som kalles over gRPC aldri motta det eksterne OIDC-tokenet, men et kortlevd internt token utstedt av kjernen.

## Beslutning

1. Vi bruker **Keycloak** som selvdriftet OIDC-leverandør.
2. I lokal utvikling kjøres Keycloak i Docker Compose med en ferdigkonfigurert realm (`erp-dev`) og en demo-tenant og demo-bruker.
3. Klienten autentiserer mot Keycloak og sender det eksterne OIDC Bearer-tokenet til kjernens token-endepunkt (`POST /api/v1/auth/token`).
4. Kjernen validerer det eksterne JWT-tokenet mot OIDC-leverandøren, slår opp bruker, tenant, roller og tilknyttede selskaper i databasen under aktiv tenant-kontekst, og utsteder et **kortlevd internt JWT** (standard levetid 15 minutter) signert av kjernen.
5. Alle påfølgende kall mot kjerne-API-et (og senere gRPC-kall videre til app-containere) bruker det interne tokenet eller valideres gjennom kjernens autentiseringslag som sikrer at `app.tenant_id` alltid settes fra validert tenant-kontekst.

## Konsekvenser

- **Fordeler:** Keycloak er modent, støtter OIDC/SAML-federasjon mot Entra ID ut av boksen, og kan eksportere/importere realms deklarativt i Docker Compose.
- **Ulemper:** Keycloak (Java/Quarkus) bruker noe mer minne i lokal Docker Compose enn lettvektsalternativer.

