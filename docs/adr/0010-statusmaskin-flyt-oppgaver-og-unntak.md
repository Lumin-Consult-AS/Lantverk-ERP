# ADR-0010: Statusmaskin, godkjenningsflyt, oppgaver og unntaksstyrt startside

- **Status:** Vedtatt
- **Dato:** 2026-10-30
- **Sak:** LUM-12 (Steg 8)

## Kontekst

Steg 8 skal gi godkjenningsflyter som kunden kan konfigurere selv, oppgaver i en arbeidsliste, godkjenning fra e-post, og en startside som viser det som krever handling. Statusmaskinen (`status` i manifestet, `locksRecord`) fantes fra før; flyt, oppgaver og `attention` var bare delvis forberedt (parsing av `attention`, `WhenRuleEvaluator`).

## Beslutning

### Statusmaskin og flyt
1. En transisjon med `workflow: <id>` flytter posten til målstatus og starter flyten **i samme transaksjon**. Flytens `onComplete`/`onReject` er navn på transisjoner i samme statusmaskin og utføres av motoren når flyten er ferdig.
2. **Flytdefinisjoner er tenantens data** (`core.workflow_definition`, JSON). Appen leverer standarder i manifestet (`workflows:`) som kopieres inn ved installasjon. Når tenanten redigerer en flyt, merkes den `is_customized` og overskrives ikke av app-oppdateringer eller ominstallasjon. Tenantens tilpasning blir stående når appen avinstalleres; app-standarder som ikke er tilpasset fjernes.
3. **`when`-regler** er en JSON-vennlig type (`field`, `operator`, `value`, eller `and`/`or`) som konverteres til eksisterende `FilterNode` og evalueres av `WhenRuleEvaluator`. Kun steg hvis regel holder for posten opprettes; uten gjeldende steg fullføres flyten umiddelbart (`onComplete` utføres).
4. **Sekvensiell og parallell** modus. Alle steg opprettes som oppgaver ved start (første `open`, resten `waiting` ved sekvensiell); avvisning avbryter øvrige oppgaver og kjører `onReject`. Parallelle *grener med sammenføyning* er fortsatt utsatt.
5. **Låsing.** Mens en instans kjører kan posten ikke slettes, kan ikke transisjoner kalles direkte, og bare felt merket `editableInWorkflow` kan endres. Låsen er en unik delvis indeks (`status = 'running'`) per post, og håndheves i kjernens data-API.
6. Instansen lagrer en **kopi av definisjonen** ved start, slik at redigering av flyten ikke endrer pågående flyter.

### Oppgaver og tildeling
7. `core.task` har mottaker som **bruker eller rolle** (en rolleoppgave kan behandles av alle med rollen), frist, lenke og kommentar. Bare mottaker kan godkjenne, avvise eller delegere; dette sjekkes i kjernen. Delegering krever `delegation: allowed`.
8. **Frist og eskalering:** `TaskDeadlineWorker` henter forfalte oppgaver på tvers av tenanter med `SECURITY DEFINER`-funksjonen `core.claim_overdue_tasks` (samme mønster som `claim_due_deliveries`, ADR-0009) og flytter dem til `deadline.escalateTo`. Hver oppgave eskaleres én gang.
9. **Mottaker `dimension-owner` støttes ikke ennå** (dimensjoner finnes ikke i fase 1). Slike definisjoner avvises ved validering med tydelig feilmelding.

### Revisjonsspor og hendelser
10. `workflow_definition`, `workflow_instance` og `task` får standard append-only `_audit`-tabell via trigger. Hver godkjenning/avvisning skrives i samme transaksjon, med hvem, når og kommentar.
11. `core.workflow.started@1`, `core.workflow.stepcompleted@1` og `core.workflow.completed@1` publiseres via outboxen (ADR-0009) med posten som aggregat-ID. Statusendringer via transisjoner publiserer nå også `<objekt>.updated@1`.

### E-postgodkjenning
12. Mottakere får e-post (SMTP; Mailpit lokalt, ellers kun logging) med **signerte lenker** (`base64url(payload).base64url(HMAC-SHA256)`; tenant, oppgave, bruker, handling, utløpstid; 7 dager). `GET` viser bare et bekreftelsesskjema, slik at e-postskannere og lenkeforhåndsvisning ikke utfører handlingen; selve handlingen skjer på `POST`. Handlingen utføres som lenkens bruker med brukerens gjeldende roller, gjennom samme regler som i arbeidslisten.
13. Utsending skjer **etter commit og etter beste evne**: en feilet e-post påvirker aldri flyten, oppgaven vises uansett i arbeidslisten. Nøkkelen (`Email:LinkSigningKey`) må byttes utenfor utvikling.

### Unntaksstyrt startside (`attention`)
14. Kilder: kjernens egne åpne oppgaver (`core.task.mine`, «Til godkjenning», alvorlighet *action*) og appers deklarative spørringer (`source.query` + `filter`, med `{me}` og `{today}`). Elementer uten treff vises ikke.
15. **Cache:** tellinger caches per (tenant, bruker, element) i 60 sekunder og tømmes for hele tenanten ved enhver committet hendelse.
16. Brukeren kan **skjule** eller **utsette** et element (`core.attention_dismissal`).
17. **Målgruppe via tillatelser** (`audience.permission`) evalueres av `PermissionService` (ADR-0011): et tillatelsessett-ID eller `objekt.handling` som brukeren må inneha gjennom rollene sine. Kilden telles dessuten bare hvis brukeren har `read` på objektet.

## Avgrensninger (bevisst utsatt eller ikke dekket)
- **`OnAttention`-hook** (kilde som krever beregning i appen) er ikke bygget; kun deklarative spørringer.
- Tillatelser på objekter og transisjoner var ikke håndhevet i data-API-et i steg 1–7; det er løst i ADR-0011.
- Visuell flytdesigner, parallelle grener med sammenføyning og flyter over flere objekter (utsatt i planen).

## Konsekvenser
- **Fordeler:** godkjenningsflyter uten utvikler; låsing og revisjon håndheves i databasen/kjernen; hendelser og e-post kobles på uten ny infrastruktur.
- **Ulemper:** rolleoppgaver kan ses av alle med rollen (mottaker er et rollenavn, ikke et tillatelsessett); e-post er best effort; tellinger kan være opptil 60 sekunder gamle for endringer uten hendelse.
