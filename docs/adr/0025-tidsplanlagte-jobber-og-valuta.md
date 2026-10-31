# ADR-0025: Tidsplanlagte jobber, valuta i regnskapet og daglig kursoppdatering

Status: akseptert (31. oktober 2026). Valutadelen skal gjennomgås av regnskapsfører (B2).

## Tidsplanlagte jobber (kjernen)

- En app kan deklarere `schedules` i manifestet: `id`, `command` (en av appens egne kommandoer) og enten `at: "HH:mm"` (daglig, UTC) eller `everyMinutes` (minst 5). Bare kodede apper kan ha dem, og kommandoen må finnes.
- Kjernen kjører kommandoen uten argumenter, samme vei som frakoblede kommandoer (gRPC `RunCommand`). Kjernen vet ikke hva den gjør.
- En arbeider (`ScheduleWorker`, hvert 30. sekund) finner tenanter med planlagte apper og kjører det som har forfalt. Tilstanden (neste kjøring, siste resultat) ligger i `core.app_schedule` per tenant, med RLS. Et nytt oppsett er forfalt med en gang, så første kjøring skjer straks etter installasjon.
- En kjøring tas («claimes») ved at neste tidspunkt skyves 15 minutter frem. Lykkes den, flyttes planen til neste vanlige tidspunkt; feiler den, forfaller den igjen etter 15 minutter (ny forsøk). To arbeidere kjører derfor ikke samme jobb samtidig. Kommandoer må tåle å kjøres flere ganger.
- Jobbene kjører med tenantens administratorrolle, siden det ikke er noen person bak. Appen er den samme koden som uansett har tilgang til tenantens data gjennom data-API-et.
- `GET /api/v1/schedules` viser jobbene og siste resultat; `POST /schedules/{app}/{id}/run` kjører en nå (kun administrator).

- **Oversikt, start/stopp og logg (LUM-30).** `core.app_schedule.enabled` lar en administrator stoppe en jobb: arbeideren hopper over den, men «Kjør nå» virker fortsatt. Hver kjøring (tidsplan eller manuell) logges i `core.app_schedule_run` med status, melding/feil og hva som startet den; de siste 200 per jobb beholdes. API: `POST /schedules/{app}/{id}/start|stop` og `GET /schedules/{app}/{id}/runs`. Klienten har siden «Planlagte jobber» (kun administrator).

## Valuta i regnskapet (`finance`)

- **Bokføringsvalutaen** er en innstilling per selskap (`finance.settings`), fordi apper ikke når selskapsdata i kjernen. Uten innstilling avvises linjer i fremmed valuta med beskjed om å sette den.
- En linje i fremmed valuta oppgis som **valuta, beløp i valuta og side** (debet/kredit). Appen finner kursen på **bilagsdatoen** (nyeste kurs på eller før datoen for paret valuta → bokføringsvaluta; finnes bare det omvendte paret, brukes 1/kurs), og regner ut debet eller kredit i bokføringsvalutaen (avrundet til to desimaler). En kurs satt på linjen går foran oppslaget. Kursen lagres på linjen og beholdes når beløpet senere endres.
- Ved bokføring kontrolleres hver linje i fremmed valuta mot kursen (beløp i valuta × kurs = debet eller kredit), i tillegg til at hvert bilag har debet = kredit i bokføringsvalutaen. Rundingsdifferanser mellom linjer må rettes med en egen avrundingslinje.
- Utsatt til etter regnskapsførers gjennomgang: realisert og urealisert valutagevinst og -tap.

## Daglig kursoppdatering (`fx.norgesbank`)

- En egen adapter-app henter Norges Banks åpne kurser (SDMX-JSON, bankdager, mot NOK) og lagrer dem som `core.exchangerate` med kilde «Norges Bank». Den kjører daglig 15:30 UTC (etter publisering ca. kl. 16 norsk tid, sommer og vinter).
- Valutaene styres av `fx.setting` (standard: EUR, USD, SEK, DKK, GBP). Kurser som er lagret fra før røres ikke, og hver kjøring fyller hull de siste 14 dagene (helligdager, en dag tjenesten var nede). Valutaer notert per 100 enheter (som JPY) omregnes til per enhet.
- Appen er den eneste som når det eksterne stedet (`data.norges-bank.no:443` i egress-listen). Kilden ligger bak et grensesnitt (`IExchangeRateFeed`), så andre kilder, for eksempel ECB, kan komme som egne apper uten å røre regnskapet.

## Konsekvenser

- Planlagte jobber er generelle og kan brukes av alle apper (varsling, import, purring).
- Uten kurs for en dato må brukeren legge den inn selv; oppslaget bruker alltid siste kurs før datoen, så helg og helligdag dekkes av fredagens kurs.
- Tester: tidsplanlegging med styrt klokke (daglig, intervall, retry, kjør nå, avinstallasjon), valuta (oppslag, omvendt kurs, manuell kurs, avvist avvik) og feeden mot et ekte meldingseksempel.
