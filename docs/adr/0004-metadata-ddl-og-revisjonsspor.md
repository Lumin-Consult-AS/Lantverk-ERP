# ADR 0004: Metadata-drevet DDL uten lange låser og uforanderlig revisjonsspor

- **Status:** Vedtatt
- **Dato:** 2026-10-30
- **Sak:** LUM-6

## Kontekst

I Steg 2 (LUM-6) skal objekter definert som data (YAML/metadata) bli til fysiske PostgreSQL-tabeller i ett skjema per app (`<app>.<objekt>`), kunne leses og skrives via et generisk data-API over `Record`, og logge enhver endring til en append-only `<tabell>_audit`-tabell via databasetrigger.

## Beslutning

1. **Skjema og tabellmapping:**
   - Hvert objekt har stabil ID `<app>.<objekt>` (f.eks. `core.party`, `demo.contactnote`) og opprettes som `<app>."<objekt>"` i PostgreSQL.
   - Felt eid av samme app som objektet får kolonnenavn `<felt_snake_case>`. Felt lagt til av en annen app (utvidelse) får kolonnenavn `<utvider_app>_<felt_snake_case>` som egne kolonner i samme tabell (aldri i en JSON-pose).
   - Felttypen `money` (beløp + valuta) lagres som to fysiske kolonner (`<kolonne>_amount numeric(18,4)` og `<kolonne>_currency varchar(3)`) og eksponeres i `Record` som `{ "amount": ..., "currency": "..." }`.

2. **DDL uten lange låser:**
   - Nye kolonner legges til med `ALTER TABLE ... ADD COLUMN IF NOT EXISTS ... NULL` uten volatile standardverdier i DDL, slik at PostgreSQL kun oppdaterer systemkatalogen uten å skrive om tabellen. Påkrevd-validering (`required: true`) og standardverdier håndheves i kjernens data-API.
   - Indekser opprettes med `CREATE [UNIQUE] INDEX CONCURRENTLY IF NOT EXISTS` utenfor transaksjonsblokk.
   - DDL utføres av kjernen over migrasjonsforbindelsen (`erp_migrator`), mens all dataaksess utføres av `erp_app` under RLS.

3. **Append-only revisjonsspor (`<tabell>_audit`):**
   - For hver objekttabell opprettes `<app>."<objekt>_audit"` med `audit_id`, `record_id`, `tenant_id`, `operation` (`INSERT`/`UPDATE`/`DELETE`), `old_data` (`jsonb`), `new_data` (`jsonb`), `changed_by` (`uuid`) og `changed_at` (`timestamptz`).
   - En felles triggerfunksjon `core.capture_audit_row()` kjører `AFTER INSERT OR UPDATE OR DELETE` på hovedtabellen og skriver til `<tabell>_audit`.
   - `erp_app` gis kun `SELECT, INSERT` på `<tabell>_audit` (aldri `UPDATE`, `DELETE` eller `TRUNCATE`). I tillegg legges en `BEFORE UPDATE OR DELETE OR TRUNCATE`-trigger (`core.prevent_audit_modification()`) på hver audit-tabell som avviser ethvert forsøk på endring eller sletting.

## Konsekvenser

- **Fordeler:** Vanlige SQL-typer, indekser og fremmednøkler uten nedetid ved skjemaendringer; uomgåelig revisjonsspor håndhevet av PostgreSQL.
- **Ulemper:** `CREATE INDEX CONCURRENTLY` kan ikke kjøres inne i en transaksjonsblokk, så skjemaprovisjonering må kjøre tabell/kolonne-DDL og indeks-DDL i adskilte kommandosteg.

