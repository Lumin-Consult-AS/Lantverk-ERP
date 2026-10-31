# ADR-0023: Hooks ved statusoverganger, og nøstede datakall fra hooks

Status: akseptert (31. oktober 2026)

## Kontekst

Regnskapsappen bokfører et bilag ved statusovergangen `post`. Det krever at appen får kontrollere overgangen (balanse, periode, kontoer) og lage bokførte poster i samme transaksjon. Hooks (`OnValidate`, `OnBeforeSave`, `OnAfterSave`) ble til nå bare kalt ved opprettelse og vanlige oppdateringer, ikke ved overganger. I tillegg ga et datakall fra en hook som selv utløste en ny hook (for eksempel «opprett linje» som utløser validering av linjen, som leser bilaget) vranglås: porten som serialiserer kall på transaksjonens ene forbindelse ble holdt av det ytre kallet mens det ventet på appen.

## Beslutning

- Statusoverganger kaller de samme hookene som andre endringer, innenfor overgangens transaksjon: `OnValidate` og `OnBeforeSave` før (med posten slik den blir, status satt til målstatusen) og `OnAfterSave` etter. Operasjonen er `transition:{navn}`. Gjelder både overganger brukeren gjør og overganger flytmotoren utfører. En hook som avviser, stopper overgangen.
- Et datakall som kjøres fra en hook slipper porten mens det venter på appen, og tar den igjen etterpå. Appens egne datakall i samme transaksjon kan da kjøre, også flere nivåer ned.
- En feil appens datakall får fra kjernen (ugyldig verdi, brudd på unik nøkkel, ikke funnet) kommer ut til den som utløste endringen som en lesbar valideringsfeil, ikke som 500. Det samme gjelder brudd på unike nøkler, referanser og uforanderlighet generelt (`TranslateDatabaseError`).

## Konsekvenser

- Hookene har fortsatt en tidsfrist (5 sekunder). Et bilag med svært mange linjer kan nå den fordi bokføringen gjør flere datakall per linje. Tidsfristen kan økes i konfigurasjonen; en samlet «bokfør-operasjon» i ett kall er neste forbedring hvis det trengs.
- Overganger i andre apper får de samme hookene; en app som ikke har hooks på objektet merker ingenting.
