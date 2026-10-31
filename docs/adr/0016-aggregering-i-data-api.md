# ADR-0016: Aggregering i data-API-et

Status: akseptert (31. oktober 2026)

## Kontekst

Regnskap og rapporter trenger summer og gruppering (saldobalanse, reskontro). Å hente alle rader og summere i appen er tregt og ville gitt hver app sin egen versjon av samme logikk.

## Beslutning

Data-API-et i kjernen får generell aggregering: sum, antall, minimum, maksimum og gjennomsnitt, med gruppering på felt og samme filter som vanlig spørring. Tillatelser og tenant-/RLS-regler gjelder som for alle andre spørringer (regel 3 og 4). Kjernen vet ikke hva en konto eller en postering er; den summerer felt på objekter.

Grensesnitt: `POST /api/v1/{company}/{objectId}/aggregate` med `measures` (`function`, `field`, `as`), `groupBy`, `filter` (samme filterspråk som spørring) og `top`. Resultatet er `rows`, der hver rad har gruppefeltene og målenes alias som nøkler. Summering og gjennomsnitt gjelder tall- og pengefelt (`felt.amount` for beløpet); gruppering kan ikke gjøres på tekst- og json-felt. Alias valideres, og alle feltnavn løses mot metadata, så ingen verdier fra kalleren havner i SQL.

## Konsekvenser

- Alle apper kan bruke aggregering, ikke bare regnskap.
- Tester beviser at aggregering ikke lekker data mellom tenanter.
- Utvides bare etter behov (regel 8): ingen vinduesfunksjoner eller egne rapportspråk nå.
