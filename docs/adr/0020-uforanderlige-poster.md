# ADR-0020: Uforanderlige poster

Status: akseptert (31. oktober 2026)

## Kontekst

Regel 5: bokførte transaksjoner er uforanderlige, og korreksjon skjer med motpostering. Det må holde for alle tilgangsveier, ikke bare for API-et, fordi bokføringsloven krever at bokførte opplysninger ikke kan endres.

## Beslutning

- Statusmaskinen i manifestet får `immutableIn: [posted]`: en liste over statuser der posten verken kan endres eller slettes.
- Håndhevingen er en radtrigger i databasen (`BEFORE UPDATE OR DELETE`) som leser statuskolonnen i den gamle raden. Også applikasjonsrollen og direkte SQL avvises (SQLSTATE 23000). Overgangen *til* den uforanderlige statusen er en vanlig oppdatering av en rad som ennå ikke er uforanderlig, og er dermed tillatt.
- Data-API-et oversetter feilen til en lesbar valideringsfeil («immutable record …»).
- Korreksjon skjer med en ny post (motpostering). Kjernen vet ikke hva det betyr for et gitt objekt; appen bestemmer.

## Konsekvenser

- Datamigreringer ved oppgradering (ADR-0013) kan ikke endre poster i en uforanderlig status. Slike migreringer må skrives som nye poster eller utelate dem.
- Å fjerne `immutableIn` i en senere versjon fjerner triggeren, men det er en brytende endring som må vurderes som major.
- Tester dekker API, direkte SQL som applikasjonsrollen, at utkast fortsatt kan endres og slettes, og at korreksjon skjer med ny post.
