# ADR-0017: Lisens og prising per app utsettes til før første pilot

Status: akseptert (31. oktober 2026)

## Kontekst

`docs/plan.md` sier at prising per app må inn i kjernen. Ingenting skal selges før avklaringen med Knowit er gjort, og det finnes ingen pilotkunde ennå.

## Beslutning

Lisens- og abonnementstjenesten bygges ikke i fase 2. Den skal være på plass før første pilot, og tas som egen port-sak da. Manifestet og app-installasjonen er laget slik at en lisenssjekk kan legges til ved installasjon uten å endre apper.

## Konsekvenser

- Fase 2 kan bruke kapasiteten på regnskap.
- Risiko: å glemme det. Derfor er det et eksplisitt krav før pilot, og det skal opprettes en Linear-sak når piloten nærmer seg.
