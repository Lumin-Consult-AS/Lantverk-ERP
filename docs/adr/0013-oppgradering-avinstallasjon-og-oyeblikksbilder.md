# ADR-0013: Oppgradering, avinstallasjon med arkiv og metadata-øyeblikksbilder

- **Status:** Vedtatt
- **Dato:** 2026-10-30
- **Sak:** LUM-14 (Steg 10)

## Kontekst

Til og med steg 9 var en «oppgradering» bare en ominstallasjon: ingen semver-sjekk, ingen datamigrasjoner, ingen opprydding av foreldreløse kolonner, og avinstallasjon lot data ligge uten spor i systemet. Fase 1-porten krever at apper kan utvikle seg over tid uten nedetid, og at en app kan fjernes uten spor. Alle tabeller og kolonner er **delt mellom tenanter** (ett skjema per app, `tenant_id` på rader), så livssyklusen må ta hensyn til at andre tenanter kan bruke det samme.

## Beslutning

### Versjonsregler (håndheves av kjernen før noe endres)
1. Ny versjon må være **høyere** enn den installerte. Samme pakke igjen er en no-op (sammenlignet på manifestinnhold, siden zip-filer får ny hash hver gang); samme versjon med annet innhold og nedgradering avvises.
2. Endringer klassifiseres (`AppUpgradeAnalyzer`). **Additive** (minor): nye felt (valgfrie, eller med standardverdi), enum-verdier, objekter og hendelser. **Brytende** (major): fjernet felt, objekt, enum eller enum-verdi eller publisert hendelse; endret type, enum, referanse; smalere lengde/skala; nytt påkrevd felt uten standardverdi.
3. Brytende endringer er bare tillatt i en **ny major**, og en ny major **krever en migrasjon** i `migrations` (`from`, `to`, `run`).
4. Ved ny major må **hver installert app som avhenger av den** støtte den nye versjonen (`dependencies`-området); ellers nektes oppgraderingen med navn på appene.
5. Migrasjonsdeklarasjoner valideres (stigende versjoner, ikke nyere enn appens versjon).

### Datamigrasjoner
6. En migrasjon er en metode i appen merket `[Migration("Migrations.V110")]`. Den kjøres av kjernen **per tenant, i steg (batcher) over gRPC** (`AppService.RunMigration`), og skriver data gjennom data-API-et med brukerens tillatelser. Appen svarer med `completed` eller et **sjekkpunkt** å fortsette fra.
7. Kjernen lagrer fremdriften etter **hvert steg** i `core.app_migration_run` (status, steg, antall poster, sjekkpunkt, forsøk, feil). Feiler en migrasjon, er fremdriften beholdt, den installerte versjonen er fortsatt den gamle, og appen virker. **Å kjøre oppgraderingen på nytt gjenopptar fra siste sjekkpunkt** i stedet for å starte på nytt; migrasjoner som er ferdige kjøres ikke igjen. Migrasjoner må være idempotente.
8. **Uten nedetid:** metadata i to faser. Før migrasjonen settes **gammelt og nytt sammen** (union), slik at migrasjonen kan lese det som fjernes og skrive det nye, og DDL er bare additiv. Etter at migrasjonene er fullført, settes den nye versjonens metadata alene, og det gamle pensjoneres. Versjonen byttes først til slutt.
9. Det som en ny versjon ikke lenger deklarerer pensjoneres uten å slette noe: **fjernede felt blir foreldreløse kolonner**, og **fjernede objekter arkiveres**.

### Avinstallasjon og karens
10. Avinstallasjon **arkiverer** appens objekter i `core.app_archive` for en karensperiode (`Apps:ArchiveRetentionDays`, standard 30 dager): dataene ligger igjen, men er usynlige. **Ominstallasjon innen fristen gjenoppretter** dem. En administrator kan avinstallere med `?purge=true` eller rense et arkiv før fristen.
11. Etter karensen sletter `AppRetentionWorker` tenantens rader (med `DELETE` under RLS, så revisjonssporet i `_audit` skrives). **Tabellen droppes først når ingen tenant har appen og den er tom**, ellers ville en tenants avinstallasjon slettet en annens data.
12. **Foreldreløse kolonner** (felt appen la på andres objekter, eller felt en oppgradering fjernet) følger samme regel: ved karensens slutt nullstilles tenantens verdier; selve kolonnen droppes bare når **ingen annen tenant** har feltet eller en pågående karens for det. Det avgjøres med `SECURITY DEFINER`-funksjonen `core.metadata_object_definitions` (samme mønster som ADR-0003/0009).
13. DDL for drop går via migrasjonsrollen (`erp_migrator`), aldri via applikasjonsrollen.

### Øyeblikksbilder
14. Kjernen tar et øyeblikksbilde av tenantens samlede metadata ved hver installasjon, oppgradering og avinstallasjon (fantes fra før). Nå kan det spørres: `GET /metadata/snapshots/{nr}`, `GET /metadata/snapshots/at?at=…&objectId=…` («hva så skjemaet ut som da») og `GET /metadata/snapshots/diff?from=&to=`.

### Tilgang
15. Installasjon, oppgradering, avinstallasjon og opprydding krever **`tenant.admin`** (tidligere kunne enhver innlogget bruker installere apper).

## Avgrensninger
- **Revisjonsspor slettes ikke.** `_audit`-tabellene er append-only (arkitekturregel 5), så «uten spor» gjelder metadata, brukergrensesnitt og data, ikke revisjonssporet. Er tabellen droppet fordi ingen tenant bruker appen, forsvinner revisjonssporet i den tabellen med den.
- Kubernetes-behandling av containere ved oppgradering (rullering av ny versjon, nedskalering av den gamle) er ikke bygget utover det som fantes (`EnsureContainerRunning`).
- Migrasjoner kjøres for tenanten som oppgraderer. Å oppgradere en app for **alle** tenanter samtidig (utrulling) er ikke bygget.
- Klassifiseringen kjenner bare felt, enum, objekt og hendelse; endringer i sider, tillatelser og flyt regnes ikke som brytende.

## Konsekvenser
- **Fordeler:** apper kan utvikles trygt over tid; en feilet migrasjon er gjenopptakbar og synlig; data kan angres innen karensen; delte tabeller og kolonner slettes aldri på en annen tenants bekostning.
- **Ulemper:** under en åpen migrasjon er metadata midlertidig et supersett; migrasjoner må skrives idempotente; arkivert data tar plass til karensen er over.
