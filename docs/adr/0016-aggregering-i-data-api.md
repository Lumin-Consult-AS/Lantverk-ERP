# ADR-0016: Aggregering i data-API-et

Status: akseptert (31. oktober 2026)

## Kontekst

Regnskap og rapporter trenger summer og gruppering (saldobalanse, reskontro). Å hente alle rader og summere i appen er tregt og ville gitt hver app sin egen versjon av samme logikk.

## Beslutning

Data-API-et i kjernen får generell aggregering: sum, antall, minimum og maksimum, med gruppering på felt og samme filter som vanlig spørring. Tillatelser og tenant-/RLS-regler gjelder som for alle andre spørringer (regel 3 og 4). Kjernen vet ikke hva en konto eller en postering er; den summerer felt på objekter.

## Konsekvenser

- Alle apper kan bruke aggregering, ikke bare regnskap.
- Tester må bevise at aggregering ikke lekker data mellom tenanter.
- Utvides bare etter behov (regel 8): ingen vinduesfunksjoner eller egne rapportspråk nå.
