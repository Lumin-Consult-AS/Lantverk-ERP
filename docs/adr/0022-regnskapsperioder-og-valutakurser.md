# ADR-0022: Regnskapsperioder og valutakurser

Status: akseptert (31. oktober 2026)

## Beslutning

- **Perioder** (`core.organization`): `core.fiscalyear` og `core.fiscalperiod` med status åpen/stengt og overgangene `close`/`reopen`. Appen vet ikke hva en periode brukes til; regnskapsappen (`finance`) slår opp status ved bokføring og avviser bokføring i en stengt periode. Hvert selskap har sine egne år og perioder (nøkkel per selskap).
- **Perioder opprettes av kallerne** (klient, import eller regnskapsappen). En deklarativ app kan ikke generere dem selv, og kjernen skal ikke kjenne kalenderregler. Generering av 12 perioder ved opprettelse av et år kommer i regnskapsappen.
- **Valutakurser** (`core.currencies`): `core.exchangerate` med fra-valuta, til-valuta, dato og kurs (8 desimaler). Kursen sier hvor mange enheter av til-valutaen én enhet av fra-valutaen er verdt. Parret har retning, og det finnes én kurs per par og dato. Valutakodene er tre store bokstaver (ISO 4217); selve valutaene er kjerneobjekter, så kursene refererer til dem med kode.
- **Oppslag per dato**: gjeldende kurs er den nyeste kursen med dato på eller før ønsket dato (`rateDate le dato`, sortert fallende, første rad). Finnes ingen, er resultatet tomt, og kalleren avgjør hva som skjer.
- **Import**: kurser kan legges inn med vanlige opprettelser eller `$batch`. Automatisk henting (for eksempel fra Norges Bank) blir en egen adapter-app senere.

## Konsekvenser

- Beløp i fremmed valuta lagres av regnskapsappen i transaksjonsvaluta og selskapets bokføringsvaluta, sammen med kursen som ble brukt (ikke beregnet på nytt senere).
- Å åpne en stengt periode igjen er en vanlig overgang. Hvem som får lov, styres av tillatelsene (`update` på perioden). Eventuelle strengere regler (for eksempel at et stengt år ikke kan åpnes) kan komme i regnskapsappen.
