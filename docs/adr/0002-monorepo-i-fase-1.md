# ADR 0002: Monorepo-struktur i fase 1

- **Status:** Vedtatt
- **Dato:** 2026-10-30
- **Sak:** LUM-5

## Kontekst

Planen definerer fem logiske repoer (`erp-core`, `erp-client`, `erp-sdk`, `erp-apps`, `erp-samples`) med ulik synlighet (privat kjerne og egne apper, offentlig SDK og eksempelapper). I fase 1 utvikles hele plattformen av én utvikler der endringer ofte berører kjerne, SDK og eksempelapper i samme leveranse.

## Beslutning

I fase 1 samles komponentene som toppnivåmapper (`erp-core/`, `erp-client/`, `erp-sdk/`, `erp-apps/`, `erp-samples/`) i ett felles git-repo. Arkitekturgrenser håndheves strengt:

- `erp-core/` har ingen prosjektreferanser til `erp-apps/` eller `erp-samples/`.
- `erp-apps/` og `erp-samples/` skal kun referere til det offentlige SDK-et i `erp-sdk/`.

Når `erp-sdk` og `erp-samples` publiseres offentlig ved porten av fase 1 (Steg 10), speiles eller splittes disse mappene ut til egne offentlige repoer.

## Konsekvenser

- **Fordeler:** Enklere versjonskontroll, atomiske commits per steg (LUM-x), og én samlet CI-pipeline i fase 1.
- **Ulemper:** Krever disiplin på prosjektreferanser slik at ingen snarveier tas mellom mapper som senere blir separate repoer.

