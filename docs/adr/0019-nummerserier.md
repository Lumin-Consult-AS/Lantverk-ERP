# ADR-0019: Nummerserier

Status: akseptert (31. oktober 2026)

## Kontekst

Bilagsnumre, fakturanumre og lignende må være sammenhengende uten hull (bokføringsloven), og kan ikke settes av klienten. Kjernen kjenner ikke begrepet «bilag», så serien må være generell.

## Beslutning

- En nummerserie deklareres på et tekstfelt i manifestet: `numberSeries: { prefix: "B-", padding: 6, perYear: false }` (eller `numberSeries: true` for en enkel teller). Serien tilhører feltet, ikke et eget objekt (regel 8: delte serier mellom objekter kan komme senere).
- Verdien tildeles av kjernen når posten opprettes og kan hverken settes av kaller eller endres senere (API-et avviser det).
- Telleren ligger i `core.number_counter` per tenant, selskap, objektfelt og periode (år for `perYear`, ellers 0). Den økes av `core.next_number` i **samme transaksjon** som innsettingen. Feiler opprettelsen, rulles telleren tilbake, så serien får ikke hull. Samtidige opprettelser serialiseres av radlåsen på telleren.
- Applikasjonsrollen har ikke tilgang til tabellen, bare til funksjonen (SECURITY DEFINER). RLS og tenant-filter gjelder.
- Alle serier er hullfrie; det finnes ikke en «ikke-hullfri» variant, siden den ikke trengs nå.

## Konsekvenser

- Opprettelser i samme serie går gjennom én rad og kan ikke kjøres helt parallelt; det er prisen for hullfrihet og er akseptabelt for bilag og fakturaer.
- Verdien tildeles etter validerings- og lagringshooks (`OnValidate`, `OnBeforeSave`), så hooks ser ikke nummeret ennå. `OnAfterSave` og hendelsene har det.
- Tester dekker rekkefølge, at kaller ikke kan sette eller endre verdien, ingen hull ved feilet opprettelse, parallelle opprettelser og tenant-isolasjon.
