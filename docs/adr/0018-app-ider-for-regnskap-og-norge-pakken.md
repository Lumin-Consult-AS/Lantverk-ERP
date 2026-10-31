# ADR-0018: App-ID-er for regnskapsappen og Norge-pakken

Status: akseptert (31. oktober 2026)

## Beslutning

- Den landsuavhengige regnskapsappen heter `finance`. Objekter følger `finance.objekt` (for eksempel `finance.account`, `finance.journalentry`), hendelser `finance.objekt.hendelse@versjon`.
- Norge-pakken heter `norway`, med objekter og hendelser under `norway.`.
- Identifikatorer er på engelsk; synlige navn («Regnskap», «Norge-pakken») settes i i18n.

## Konsekvenser

- ID-ene er låst i manifestene og kan ikke endres uten ny major og migrasjon (regel 6). Andre land får tilsvarende pakker, for eksempel `sweden` og `denmark`.
- Produktnavnet er ikke avgjort. App-ID-ene er tekniske og uavhengige av det, så de påvirkes ikke av en senere navneendring.
