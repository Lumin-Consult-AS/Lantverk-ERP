# Demo av porten for fase 1

Porten (se `docs/IMPLEMENTATION_PLAN.md`) har sju deler. Denne veiledningen viser dem på to måter:

- **Del 1 – Start alt** (felles for resten).
- **Del 2 – Kommandoer du kjører selv** mot API-et: apper, godkjenningsflyt, hendelser, innboks med PDF, avinstallasjon og øyeblikksbilder. Kallene i denne delen er kjørt mot en ekte stack og fungerte.
- **Del 3 – Automatiske tester** for det som krever en app med egen kode (`demo.contacts`): felt og fane på partkortet, validering, hendelser, oppgradering 1.0 → 1.1 og frakoblet synk. Appen kjører i en egen container på Kubernetes, og det finnes ikke noe lokalt oppsett som starter den for hånd.
- **Del 4 – Se det i klienten.**
- **Del 5 – Feilsøking.**

Kommandoene i del 1, 2 og 4 er PowerShell (Windows). Bruk bare vanlige bokstaver (ikke æ, ø, å) i JSON-tekstene du sender, siden PowerShell 5.1 ellers kan sende feil tegnkoding.

## Del 1 – Start alt

Fra rotmappen `Laftverk ERP`:

```powershell
docker compose up -d
```

Første gang bygges `erp-core`-bildet, og det tar noen minutter. Sjekk at API-et svarer:

```powershell
Invoke-RestMethod http://localhost:5000/api/v1/health
```

Forventet: `status : healthy`.

Logg inn som demo-bruker og sett opp variablene som brukes i resten av del 2. Tokenet varer i 15 minutter; kjør disse linjene på nytt hvis du får 401.

```powershell
$api = "http://localhost:5000/api/v1"
$company = "11111111-1111-1111-1111-111111111101"
$token = (Invoke-RestMethod -Method Post "$api/auth/dev-login" -ContentType "application/json" -Body "{}").accessToken
$h = @{ Authorization = "Bearer $token" }
```

Demo-brukeren «Jens Demo» har rollene `tenant.admin` og `core.user`.

## Del 2 – Kommandoer du kjører selv

### 2.1 Installer apper og opprett en part

```powershell
Invoke-RestMethod -Method Post "$api/apps/install-builtin/core.parties" -Headers $h
Invoke-RestMethod -Method Post "$api/apps/install-builtin/demo.approvals" -Headers $h
Invoke-RestMethod "$api/apps" -Headers $h | Select-Object appId, version, isDeclarative
```

Forventet: begge appene i listen. Får du «already installed», er appen allerede installert, og du kan gå videre.

Opprett en part (grunnappen `core.parties`):

```powershell
$party = Invoke-RestMethod -Method Post "$api/$company/core.party" -Headers $h -ContentType "application/json" `
  -Body '{"partyNumber":"K-100","name":"Demo Kunde AS","partyType":"organization","role":"customer","organizationNumber":"955777888"}'
$party.id
```

Bruker du et partnummer som finnes fra før, får du en feilmelding; velg da et nytt.

### 2.2 Godkjenningsflyt med to steg og beløpsgrense

`demo.approvals` har en flyt: steg 1 (avdelingsleder) gjelder alle beløp over 0, steg 2 (økonomisjef) bare over 50 000. Demo-brukeren har ikke rollene `demo.manager` og `demo.finance`, og det finnes ikke noe API for å gi roller. For å kunne godkjenne selv overstyrer vi flyten for tenanten, slik at begge stegene går til rollen `tenant.admin`. Overstyring er en vanlig funksjon: en tenant kan alltid overstyre en apps standardflyt.

```powershell
$flow = @'
{"object":"demo.expense","mode":"sequential","delegation":"none","onComplete":"approve","onReject":"reject",
 "steps":[
  {"name":"Avdelingsleder","assignee":{"role":"tenant.admin"},"when":{"field":"amount","operator":"gt","value":0}},
  {"name":"Okonomisjef","assignee":{"role":"tenant.admin"},"when":{"field":"amount","operator":"gt","value":50000}}]}
'@
Invoke-RestMethod -Method Put "$api/workflows/demo.expense.approval" -Headers $h -ContentType "application/json" -Body $flow
```

Forventet: ingen utskrift (HTTP 204).

Opprett et utlegg på 80 000 og send det til godkjenning:

```powershell
$expense = Invoke-RestMethod -Method Post "$api/$company/demo.expense" -Headers $h -ContentType "application/json" `
  -Body '{"title":"Reise til Bergen","amount":80000}'
$expense.fields.status          # draft
Invoke-RestMethod -Method Post "$api/$company/demo.expense/$($expense.id)/submit" -Headers $h | Out-Null
(Invoke-RestMethod "$api/$company/demo.expense/$($expense.id)" -Headers $h).fields.status    # pending
```

Se oppgaven, og «Til godkjenning» på startsiden (attention):

```powershell
Invoke-RestMethod "$api/tasks" -Headers $h | Where-Object status -eq open | Select-Object id, title, assigneeRole
Invoke-RestMethod "$api/attention" -Headers $h
```

Godkjenn steg 1. Da åpnes steg 2, fordi beløpet er over 50 000:

```powershell
$t1 = Invoke-RestMethod "$api/tasks" -Headers $h | Where-Object { $_.status -eq "open" -and $_.recordId -eq $expense.id }
Invoke-RestMethod -Method Post "$api/tasks/$($t1.id)/approve" -Headers $h -ContentType "application/json" -Body '{"comment":"OK fra avdelingsleder"}' | Select-Object title, status

$t2 = Invoke-RestMethod "$api/tasks" -Headers $h | Where-Object { $_.status -eq "open" -and $_.recordId -eq $expense.id }
$t2.title                       # Okonomisjef: Reise til Bergen
Invoke-RestMethod -Method Post "$api/tasks/$($t2.id)/approve" -Headers $h -ContentType "application/json" -Body '{}' | Select-Object title, status
(Invoke-RestMethod "$api/$company/demo.expense/$($expense.id)" -Headers $h).fields.status    # approved
```

Forventet: utlegget er `approved` og låst for videre redigering.

Avvisning: send inn et nytt utlegg og avvis oppgaven. Utlegget går tilbake til `draft`.

```powershell
$e2 = Invoke-RestMethod -Method Post "$api/$company/demo.expense" -Headers $h -ContentType "application/json" -Body '{"title":"Lunsj","amount":900}'
Invoke-RestMethod -Method Post "$api/$company/demo.expense/$($e2.id)/submit" -Headers $h | Out-Null
$t = Invoke-RestMethod "$api/tasks" -Headers $h | Where-Object { $_.status -eq "open" -and $_.recordId -eq $e2.id }
Invoke-RestMethod -Method Post "$api/tasks/$($t.id)/reject" -Headers $h -ContentType "application/json" -Body '{"comment":"For dyrt"}' | Select-Object status
(Invoke-RestMethod "$api/$company/demo.expense/$($e2.id)" -Headers $h).fields.status    # draft
```

### 2.3 Hendelser

Flyten og dataendringene over har publisert hendelser. Se dem i loggen:

```powershell
Invoke-RestMethod "$api/events?top=10" -Headers $h | Select-Object eventName, aggregateId
Invoke-RestMethod "$api/events/dead-letters" -Headers $h
```

Forventet: blant annet `core.workflow.stepcompleted@1`, og tom liste med dead-letters. Levering til `demo.contacts` og til en webhook, og avspilling på nytt, vises av `EventBusTests` (del 3).

### 2.4 Dokumentmottak: PDF i innboksen, knyttet til en part

Innboksen skanner filer med ClamAV. ClamAV ligger i en egen profil og må startes. Første oppstart laster ned virusdefinisjoner og kan ta flere minutter; vent til den er `healthy`:

```powershell
docker compose --profile documents up -d clamav
docker inspect -f '{{.State.Health.Status}}' erp-clamav
```

Lag en liten test-PDF og last den opp i innboksen. Opplastingen bruker `curl.exe`, siden PowerShell 5.1 ikke kan sende skjemaopplasting selv.

```powershell
$pdf = Join-Path $env:TEMP "faktura.pdf"
Set-Content -Path $pdf -Value "%PDF-1.4`n1 0 obj<</Type/Catalog>>endobj`ntrailer<</Root 1 0 R>>`n%%EOF`n$(Get-Date -Format o)" -Encoding ascii

$inbox = (Invoke-RestMethod "$api/inboxes" -Headers $h)[0]
$inbox.address
curl.exe -s -X POST "$api/inboxes/$($inbox.id)/items" -H "Authorization: Bearer $token" -F "subject=Faktura fra leverandor" -F "file=@$pdf;type=application/pdf"
```

Forventet: et element med `status: scanning`. Etter noen sekunder er skanningen ferdig:

```powershell
Start-Sleep 15
$item = Invoke-RestMethod "$api/inbox/items" -Headers $h | Where-Object subject -eq "Faktura fra leverandor" | Select-Object -First 1
$item.status        # ikke lenger scanning
```

Knytt dokumentet til parten fra 2.1 og se vedlegget på parten:

```powershell
Invoke-RestMethod -Method Post "$api/inbox/items/$($item.id)/attach" -Headers $h -ContentType "application/json" `
  -Body (@{ objectId = "core.party"; recordId = $party.id; category = "invoice" } | ConvertTo-Json) | Select-Object status, resultObjectId
Invoke-RestMethod "$api/attachments?objectId=core.party&recordId=$($party.id)" -Headers $h | Select-Object fileId, category
```

Forventet: elementet er `processed`, og vedlegget ligger på parten. Mottak av ekte e-post via Brevo (webhook), duplikatkontroll, virusavvisning og Object Lock vises av `DocumentTests` og `S3ObjectStorageTests` (del 3).

### 2.5 Avinstaller med arkiv, og øyeblikksbilder

```powershell
Invoke-RestMethod -Method Delete "$api/apps/demo.approvals" -Headers $h
Invoke-RestMethod "$api/apps" -Headers $h | Select-Object appId
Invoke-RestMethod "$api/apps/archives" -Headers $h | Select-Object appId, status, purgeAfter
Invoke-RestMethod "$api/metadata/snapshots" -Headers $h | Select-Object snapshotNumber, triggerAction, triggerAppId
```

Forventet: `demo.approvals` er borte fra appene, dataene ligger i et arkiv som slettes etter 30 dager (`purgeAfter`), og hvert installasjons- og avinstallasjonstrinn har fått et øyeblikksbilde. Installer appen igjen når du er ferdig:

```powershell
Invoke-RestMethod -Method Post "$api/apps/install-builtin/demo.approvals" -Headers $h | Select-Object appId, version
```

## Del 3 – Automatiske tester for `demo.contacts` og resten

Krever Docker. Kommandoene kjøres fra rotmappen. Stopp gjerne API-et i Docker (`docker compose stop erp-core`) hvis bygget klager på låste filer.

Hele porten, inkludert S3-testene:

```powershell
$env:ERP_TEST_S3_IMAGE = "pgsty/minio:latest"
dotnet test erp-core/Erp.Core.sln
```

Forventet: `Passed! - Failed: 0 ... Skipped: 0`.

Én del av gangen (lim inn filteret etter `--filter`):

| Del av porten | Filter | Hva som vises |
| --- | --- | --- |
| Installere `demo.contacts`, felt på `core.party`, fane på partkortet | `"FullyQualifiedName~AppInstallationTests\|FullyQualifiedName~ObjectAndPageExtensionTests"` | Appen installeres, feltet `preferredChannel` og fanen «Kontaktnotater» dukker opp og forsvinner igjen ved avinstallasjon |
| Validering og hendelser fra egen container | `"FullyQualifiedName~AppHostAndSdkTests"` | Notat med oppfølgingsdato i fortiden avvises (`OnValidate`); notater slettes når parten slettes (`OnEvent`) |
| Hendelsesbuss | `"FullyQualifiedName~EventBusTests"` | Hendelse i loggen, levert til app og webhook, spilt av på nytt |
| Godkjenningsflyt | `"FullyQualifiedName~WorkflowTests"` | To godkjennere, beløpsgrense, e-postgodkjenning, delegering |
| Dokumentmottak | `"FullyQualifiedName~DocumentTests"` | E-post med PDF havner i innboksen og knyttes til part |
| Oppgradering og avinstallasjon | `"FullyQualifiedName~AppLifecycleTests"` | `demo.contacts` 1.0 → 1.1 med datamigrering, arkiv og øyeblikksbilder |
| Frakoblet synk | `"FullyQualifiedName~SyncTests"` | Last ned sett, gå frakoblet, kommandoer med avvis/juster/manuell kø, reservasjoner, sperret enhet |
| Objektlager (S3) | `"FullyQualifiedName~S3ObjectStorageTests"` | Lagring og lås mot MinIO (krever `ERP_TEST_S3_IMAGE`) |

Eksempel:

```powershell
dotnet test erp-core/Erp.Core.sln --filter "FullyQualifiedName~SyncTests"
```

Vil du se navnet på hver test som kjører, legg til `--logger "console;verbosity=normal"`.

## Del 4 – Se det i klienten

1. Start stacken (del 1). API-et ligger på http://localhost:5000.
2. Start klienten i et eget terminalvindu:

   ```powershell
   dotnet run --project erp-client/src/Erp.Client --urls http://localhost:5100
   ```

3. Åpne http://localhost:5100 og logg inn som demo-bruker.
4. **Parter:** hvis grunnappen ikke er installert, trykk «Installer grunnappen Parter». Opprett, list og rediger en part. Parten fra 2.1 skal vises.
5. **Godkjenning:** utleggene fra 2.2 vises under `demo.approvals`. Kjør 2.2 til og med innsending, så ser du «Til godkjenning» på startsiden.
6. **Innboks:** åpne «Innboks» og se elementet fra 2.4.
7. **Avinstaller** en app og se at menyvalg og sider forsvinner.

Merk: klientdelen (del 4) er ikke klikket gjennom ved skriving; meld fra hvis et trinn ikke stemmer.

## Del 5 – Feilsøking

| Symptom | Årsak og løsning |
| --- | --- |
| `401` på alle kall | Tokenet varer 15 minutter. Kjør innloggingen i del 1 på nytt. |
| «already installed» | Appen er allerede installert. Gå videre. |
| Innboksen står på `scanning` | ClamAV kjører ikke eller er ikke ferdig. Start den (2.4) og last opp en ny fil. |
| `400` eller rare tegn i JSON | Ikke bruk æ, ø, å i kommandoene (PowerShell 5.1). |
| Klienten viser feil ved start | API-et må kjøre på port 5000 og klienten på 5100 (`--urls http://localhost:5100`). |
| `docker compose up -d` feiler ved bygging | Hent siste `main`. Bygget skal ha rotmappen som kontekst. |
| Bygg/test klager på låste filer | Stopp API-et: `docker compose stop erp-core`. |
| Nullstille alt | `docker compose down -v` sletter også databasen. Neste oppstart seeder demo-tenanten på nytt. |
