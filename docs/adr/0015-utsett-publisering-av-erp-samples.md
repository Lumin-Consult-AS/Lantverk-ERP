# ADR-0015: Publisering av erp-samples utsettes

Status: akseptert

## Kontekst

Steg 10 krevde at `erp-samples` publiseres offentlig før porten for fase 1 lukkes. Produktet er ikke ferdig, og eksemplene bør ikke gjøres offentlige før SDK-et og manifestformatet er stabile nok til at andre kan bygge på dem.

## Beslutning

Publiseringen utsettes til en senere port. Porten for fase 1 lukkes når den øvrige demoen (se `docs/DEMO_FASE_1.md`) er godkjent. Apache-2.0-lisensen ligger allerede i `erp-sdk` og `erp-samples`, så publisering senere krever ingen kodeendring.

## Konsekvenser

- Ingenting offentliggjøres nå; repoene er fortsatt private.
- Publisering krever en eksplisitt beslutning, og bør skje sammen med at SDK-et publiseres, siden eksemplene bygger mot det.
