# ADR 0003: Rad-nivå-sikkerhet (RLS) og separasjon av databaseroller i PostgreSQL

- **Status:** Vedtatt
- **Dato:** 2026-10-30
- **Sak:** LUM-5

## Kontekst

Plattformen kjører kun multi-tenant i én felles PostgreSQL-database. Arkitekturregel 4 krever at ingen databasetilgang fra applikasjonen skjer uten rad-nivå-sikkerhet (RLS), at applikasjonsrollen ikke har `BYPASSRLS`, og at `app.tenant_id` settes per transaksjon.

## Beslutning

1. Vi benytter to adskilte databaseroller:
   - `erp_migrator`: Eier skjemaer og tabeller, og kjører EF Core-migrasjoner og fremtidig metadata-DDL.
   - `erp_app`: Applikasjonsrolle med `NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS`. Har kun `SELECT, INSERT, UPDATE, DELETE` på tabeller i applikasjonsskjemaer.
2. Alle tabeller opprettes i sitt respektive app-skjema (for kjernen: `core`) med de åtte obligatoriske kolonnene: `id`, `tenant_id`, `company_id`, `created_at`, `created_by`, `updated_at`, `updated_by`, `row_version`.
3. På hver tabell aktiveres både `ENABLE ROW LEVEL SECURITY` og `FORCE ROW LEVEL SECURITY` med policyen:
   ```sql
   CREATE POLICY tenant_isolation ON <schema>.<table>
     USING (tenant_id = current_setting('app.tenant_id')::uuid)
     WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
   ```
4. Ved åpning av databaseforbindelse / start av transaksjon i `erp-core` kjøres `SELECT set_config('app.tenant_id', @tenantId, true)` (transaksjonslokalt) eller på forbindelsen før spørringer utføres. Dersom `app.tenant_id` ikke er satt til en gyldig UUID, feiler `current_setting('app.tenant_id')::uuid` (fail-closed) slik at ingen rader kan lekke ved en feil.
5. For oppslag av hvilken `tenant_id` en innkommende OIDC-bruker tilhører ved innlogging før tenant-kontekst er kjent (f.eks. fra `sub` eller tenant-slug), brukes en dedikert, minimal `SECURITY DEFINER`-funksjon (`core.resolve_login_tenant`) som kun returnerer `tenant_id` for et gitt eksternt subjekt eller tenant-kode.

## Konsekvenser

- **Fordeler:** Garantert tenant-isolasjon i databaselaget selv om applikasjonskode glemmer `.Where(x => x.TenantId == ...)`. Fail-closed oppførsel dersom tenant-kontekst mangler.
- **Ulemper:** Krever at alle databaseoperasjoner i applikasjonen og i tester kjører med aktiv tenant-kontekst og via `erp_app`-rollen.

