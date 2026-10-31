# Demo av porten for fase 1

Porten (se `docs/IMPLEMENTATION_PLAN.md`) har sju deler. Appene med egen kode (`demo.contacts`) kjører i en egen container på Kubernetes, og det finnes ikke noe lokalt oppsett som starter dem for hånd. Delene som trenger kode fra appen vises derfor ved at testene kjører hele forløpet mot en ekte PostgreSQL og en app-container i test-miljøet. Resten kan du se selv i klienten.

## Del A – Kjør hele porten automatisk

Krever Docker. Fra `erp-core`:

```
ERP_TEST_S3_IMAGE=pgsty/minio:latest dotnet test Erp.Core.sln
```

Forventet: alle tester grønne (ingen hoppet over). De som viser porten:

| Del av porten | Test |
| --- | --- |
| Installere `demo.contacts`, felt på `core.party`, fane på partkortet | `AppInstallationTests`, `ObjectAndPageExtensionTests` |
| Avvise notat med dato i fortiden (`OnValidate`) og slette notater når part slettes (`OnEvent`) | `AppHostAndSdkTests` |
| Hendelse publisert, levert til app og webhook, spilt av på nytt | `EventBusTests` |
| Flyt med to godkjennere og beløpsgrense | `WorkflowTests` |
| E-post med PDF i innboksen, knyttet til part | `DocumentTests` |
| Oppgradering 1.0 → 1.1 med migrering, avinstallasjon med arkiv | `AppLifecycleTests` |
| Frakoblet synk med konflikter etter appens regel | `SyncTests` |

Kjør én del: `dotnet test Erp.Core.sln --filter "FullyQualifiedName~SyncTests"`.

## Del B – Se det i klienten

1. `docker compose up -d` (PostgreSQL, Mailpit m.fl.).
2. Start API-et (port 5000) og klienten med `--urls http://localhost:5100`. Åpne http://localhost:5100.
3. Logg inn som demo-bruker.
4. **Installer grunnappen «Parter»** på startsiden. Opprett, list og rediger en part.
5. **Flyt med godkjenning:** installer `demo.approvals` (`erp-samples/demo.approvals`), opprett en utgift over beløpsgrensen. «Til godkjenning» dukker opp på startsiden for riktig bruker, i to steg.
6. **Innboks:** send en e-post med PDF til innboksen (se oppsettet fra LUM-13) og knytt dokumentet til en part under «Innboks».
7. **Avinstaller** en app og se at fanene og sidene forsvinner.

`demo.contacts` (felt og fane på partkortet, oppgradering 1.0 → 1.1, frakoblede kommandoer) vises av testene i del A.
