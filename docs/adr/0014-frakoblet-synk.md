# ADR-0014: Frakoblet synk (LUM-15)

Status: akseptert

## Kontekst

Håndterminaler og andre klienter må kunne jobbe uten nett (fase 1-porten krever en demo av dette). Kjernen skal fortsatt ikke kjenne domenet, og regler og tillatelser skal håndheves i kjernens data-API, ikke i klienten.

## Beslutning

- **Synk-sett** deklareres i appens manifest (`syncSets`): en navngitt liste med objekter, hver med valgfritt filter. Kjernen kjenner bare objekt-ID-er og filteruttrykk.
- **Nedlasting**: første pull er et øyeblikksbilde i sider (fortsettelsestoken), deretter delta-pull med markør `"xid.seq"` fra `core.change_log`. Loggen skrives av en trigger på alle objekttabeller. Kun endringer med `xid < pg_snapshot_xmin(pg_current_snapshot())` leveres, slik at en transaksjon som committer sent aldri havner bak en markør. Poster som slettes eller forlater settet leveres som `removed`. Loggen ryddes etter 90 dager (`ChangeLogRetention`); en enhet som har vært borte lenger må laste ned på nytt.
- **Kommandokø**: klienten lagrer kommandoer med egen id og baseversjon (`row_version`) og sender dem i rekkefølge. Kommandoer er deklarert i manifestet (`commands`) og kjøres av appens `[Command]`-handler via data-API-et, så hooks og tillatelser gjelder som ved online-bruk. Samme id sendt på nytt gir lagret svar uten ny kjøring (idempotens).
- **Konfliktregel per kommando** (`onConflict`): `reject` (status `conflict`), `adjust` (handleren kjøres med `HasConflict`, og avgjør selv) eller `manual` (kø der en administrator velger å utføre eller forkaste). Reservert av en annen enhet gir alltid `conflict`.
- **Egne endringer** i samme opplasting: kjernen husker versjonen en enhets tidligere kommando ga, slik at den ikke gir falske konflikter. Det gjelder bare kommandoer som kjørte uten konflikt.
- **Reservasjoner**: én per post, med utløpstid; kan tas over når den er utløpt eller av samme enhet.
- **Enheter**: registreres per bruker; administrator kan sperre og åpne sentralt. Ved (re)tilkobling får enheten et kortlevd token.

## Konsekvenser

- Kjernen er fortsatt domene-uavhengig; all forretningsregel ligger i appens handlere.
- Alle tabellene har RLS og revisjonsspor der det er relevant; tenant-isolasjon testes.
- SDK-et får `[Command]`, `CommandContext` og `CommandResult`; protokontrakten får `RunCommand`.
- Utsatt: kryptert lokal lagring og selve skanner-klienten (fase 3).
