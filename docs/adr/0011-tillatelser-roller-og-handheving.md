# ADR-0011: Tillatelser, roller og håndheving i data-API-et

- **Status:** Vedtatt
- **Dato:** 2026-10-30
- **Sak:** LUM-12 (hull oppdaget i Steg 8)

## Kontekst

Arkitekturregel 3 krever at tillatelser og tenant håndheves i kjernens data-API, ikke i UI eller appen. Tenant-isolasjon var håndhevet (RLS), men **tillatelser var det ikke**: appers `permissionSets` ble lagret og listet, men ingen operasjon sjekket dem, og `permission:` på statustransisjoner ble ignorert. I tillegg mistet kall fra og til apper brukerens roller (gRPC-konteksten ble satt uten roller), så håndheving ville vært umulig for apper. Hullet gjaldt alle steg fra og med steg 2.

## Beslutning

1. **Modell** (som i teknisk design): et *tillatelsessett* defineres av en app i manifestet (`objekt → handlinger`: read, create, update, delete, post, approve). En *rolle* er tenantens sammensetning av tillatelsessett. En *bruker* har rollenavn (`core.user.roles`). Koblingen rolle → tillatelsessett er tenantens data i `core.role_permission_set` (RLS, revisjonsspor).
2. **`tenant.admin` har alle tillatelser** (implisitt, kan ikke redigeres). Uten en rolle som gir tilgang nektes alt (**fail closed**). Å installere en app gir altså ingen andre enn administratorer tilgang før et tillatelsessett er koblet til en rolle.
3. **Håndheving i `RecordDataService`**, som både REST og gRPC går gjennom, slik at UI og apper ikke kan omgå den:

   | Operasjon | Krever |
   | --- | --- |
   | get, query, count, revisjonsspor | `read` |
   | create | `create` |
   | update | `update` |
   | delete (også delete-where og batch) | `delete` |
   | statustransisjon | transisjonens `permission:` (standard `update`) |

   Mangler tillatelsen, gir REST `403` og gRPC `PermissionDenied`. Sjekken skjer før objektet slås opp.
4. **Roller følger med i appkall.** Kjernen legger brukerens roller i det kortlevde tokenet den gir appen, og tokenets roller brukes når appen kaller tilbake. En app kan dermed aldri gjøre mer enn brukeren den handler på vegne av. For asynkrone hendelser (`OnEvent`) brukes rollene til brukeren som utløste hendelsen, slik de er ved levering.
5. **Systemhandlinger er unntatt:** flytmotoren utfører `onComplete`/`onReject`-transisjoner uten egen tillatelsessjekk (det er motoren som fullfører flyten brukerne allerede har godkjent), og bakgrunnsarbeidere bruker ikke data-API-et.
6. **Administrasjon** (kun `tenant.admin`): `GET /api/v1/roles` og `PUT /api/v1/roles/{rolle}/permission-sets`, med validering mot installerte tillatelsessett. `GET /api/v1/permissions/me` viser hva innlogget bruker kan, slik at klienter kan skjule handlinger som vil bli nektet.
7. **Startsidens målgruppe** (`attention.audience.permission`) bruker samme evaluering: tillatelsessett-ID eller `objekt.handling`.

## Avgrensninger
- **Objektnivå** i fase 1; feltnivå og radfilter per rolle kommer senere (som i teknisk design).
- **Roller begrenset til selskap** («eventuelt begrenset til selskap») er ikke bygget; en rolle gjelder alle selskap brukeren har tilgang til.
- **Ingen UI** for rolleadministrasjon ennå, kun API.
- **Apper som handler på brukerens vegne** nektes det brukeren ikke har rett til. Rene bakgrunnsjobber bør senere få en tjenestebruker med tildelte roller, slik teknisk design beskriver.
- `read` følger ikke implisitt av `update`; tillatelsessett må liste handlingene eksplisitt.

## Konsekvenser
- **Fordeler:** regel 3 er oppfylt for data-API-et; apper kan ikke eskalere rettigheter; endringer i roller er revisjonsført.
- **Ulemper:** nye apper er utilgjengelige for andre enn administratorer til tillatelsessett er koblet til roller; asynkrone app-handlere kan feile (og havne i dødbrevkøen) hvis brukeren som utløste hendelsen mangler tillatelse til det handleren gjør.
