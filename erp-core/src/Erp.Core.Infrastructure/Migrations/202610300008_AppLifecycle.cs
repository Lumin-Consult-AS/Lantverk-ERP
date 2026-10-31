using Erp.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Core.Infrastructure.Migrations;

/// <summary>
/// App lifecycle (ADR-0013): the progress log of data migrations run during upgrades, and the archive of data kept
/// for a grace period after an app is uninstalled. Raw SQL, RLS on both tables, audit on the archive.
/// </summary>
[DbContext(typeof(CoreDbContext))]
[Migration("202610300008_AppLifecycle")]
public partial class AppLifecycle : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE core.app_migration_run (
                id uuid PRIMARY KEY,
                tenant_id uuid NOT NULL,
                company_id uuid NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                created_by uuid NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now(),
                updated_by uuid NOT NULL,
                row_version bigint NOT NULL DEFAULT 1,
                app_id character varying(128) NOT NULL,
                from_version character varying(64) NOT NULL,
                to_version character varying(64) NOT NULL,
                migration_class character varying(200) NOT NULL,
                status character varying(16) NOT NULL DEFAULT 'running' CHECK (status IN ('running', 'completed', 'failed')),
                steps integer NOT NULL DEFAULT 0,
                records_processed bigint NOT NULL DEFAULT 0,
                checkpoint text NULL,
                attempts integer NOT NULL DEFAULT 1,
                error text NULL,
                started_at timestamptz NOT NULL DEFAULT now(),
                finished_at timestamptz NULL,
                UNIQUE (tenant_id, app_id, from_version, to_version, migration_class)
            );

            CREATE TABLE core.app_archive (
                id uuid PRIMARY KEY,
                tenant_id uuid NOT NULL,
                company_id uuid NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                created_by uuid NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now(),
                updated_by uuid NOT NULL,
                row_version bigint NOT NULL DEFAULT 1,
                app_id character varying(128) NOT NULL,
                app_version character varying(64) NOT NULL,
                reason character varying(32) NOT NULL CHECK (reason IN ('uninstall', 'upgrade')),
                object_ids jsonb NOT NULL DEFAULT '[]'::jsonb,
                archived_at timestamptz NOT NULL DEFAULT now(),
                purge_after timestamptz NOT NULL,
                status character varying(16) NOT NULL DEFAULT 'archived' CHECK (status IN ('archived', 'restored', 'purged')),
                purged_at timestamptz NULL,
                rows_purged bigint NULL
            );
            CREATE INDEX "IX_app_archive_due" ON core.app_archive (purge_after) WHERE status = 'archived';
            CREATE INDEX "IX_app_archive_app" ON core.app_archive (tenant_id, app_id);

            ALTER TABLE core.app_migration_run ENABLE ROW LEVEL SECURITY;
            ALTER TABLE core.app_migration_run FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON core.app_migration_run
                USING (tenant_id = current_setting('app.tenant_id')::uuid)
                WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
            GRANT SELECT, INSERT, UPDATE ON core.app_migration_run TO erp_app;

            ALTER TABLE core.app_archive ENABLE ROW LEVEL SECURITY;
            ALTER TABLE core.app_archive FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON core.app_archive
                USING (tenant_id = current_setting('app.tenant_id')::uuid)
                WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
            GRANT SELECT, INSERT, UPDATE ON core.app_archive TO erp_app;

            CREATE TABLE core.app_archive_audit (
                audit_id bigserial PRIMARY KEY,
                record_id uuid NOT NULL,
                tenant_id uuid NOT NULL,
                operation character varying(10) NOT NULL,
                old_data jsonb NULL,
                new_data jsonb NULL,
                changed_by uuid NOT NULL,
                changed_at timestamptz NOT NULL DEFAULT now(),
                reason text NULL
            );
            CREATE INDEX "IX_app_archive_audit_tenant_record" ON core.app_archive_audit (tenant_id, record_id, audit_id);
            ALTER TABLE core.app_archive_audit ENABLE ROW LEVEL SECURITY;
            ALTER TABLE core.app_archive_audit FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON core.app_archive_audit
                USING (tenant_id = current_setting('app.tenant_id')::uuid)
                WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
            REVOKE ALL ON core.app_archive_audit FROM PUBLIC, erp_app;
            GRANT SELECT, INSERT ON core.app_archive_audit TO erp_app;
            GRANT USAGE, SELECT ON SEQUENCE core.app_archive_audit_audit_id_seq TO erp_app;
            CREATE TRIGGER "trg_app_archive_audit" AFTER INSERT OR UPDATE OR DELETE ON core.app_archive
                FOR EACH ROW EXECUTE FUNCTION core.capture_audit_row();
            CREATE TRIGGER "trg_app_archive_audit_immutable" BEFORE UPDATE OR DELETE OR TRUNCATE ON core.app_archive_audit
                FOR EACH STATEMENT EXECUTE FUNCTION core.prevent_audit_modification();

            -- Object and column definitions are per tenant, but the physical tables and columns are shared by every
            -- tenant. Before a shared column or table is dropped the core must see all tenants; these SECURITY DEFINER
            -- functions expose only that (the same pattern as resolve_login_tenant).
            CREATE OR REPLACE FUNCTION core.metadata_object_definitions(p_object_id text)
            RETURNS TABLE (tenant_id uuid, definition jsonb)
            LANGUAGE sql
            SECURITY DEFINER
            SET search_path = core, pg_temp
            AS $$
                SELECT m.tenant_id, m.definition_json
                  FROM core.metadata_item m
                 WHERE m.kind = 'object' AND m.item_id = p_object_id;
            $$;
            REVOKE ALL ON FUNCTION core.metadata_object_definitions(text) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION core.metadata_object_definitions(text) TO erp_app;

            CREATE OR REPLACE FUNCTION core.app_installed_anywhere(p_app_id text)
            RETURNS boolean
            LANGUAGE sql
            SECURITY DEFINER
            SET search_path = core, pg_temp
            AS $$
                SELECT EXISTS (SELECT 1 FROM core.installed_app WHERE app_id = p_app_id);
            $$;
            REVOKE ALL ON FUNCTION core.app_installed_anywhere(text) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION core.app_installed_anywhere(text) TO erp_app;

            -- Tenants that have archived data or orphaned columns whose grace period has passed.
            CREATE OR REPLACE FUNCTION core.tenants_with_expired_retention()
            RETURNS TABLE (tenant_id uuid)
            LANGUAGE sql
            SECURITY DEFINER
            SET search_path = core, pg_temp
            AS $$
                SELECT a.tenant_id FROM core.app_archive a WHERE a.status = 'archived' AND a.purge_after <= now()
                UNION
                SELECT m.tenant_id
                  FROM core.metadata_item m, jsonb_array_elements(COALESCE(m.definition_json -> 'orphanedColumns', '[]'::jsonb)) o
                 WHERE m.kind = 'object' AND (o ->> 'dropAfter')::timestamptz <= now();
            $$;
            REVOKE ALL ON FUNCTION core.tenants_with_expired_retention() FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION core.tenants_with_expired_retention() TO erp_app;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP FUNCTION IF EXISTS core.tenants_with_expired_retention();
            DROP FUNCTION IF EXISTS core.app_installed_anywhere(text);
            DROP FUNCTION IF EXISTS core.metadata_object_definitions(text);
            DROP TABLE IF EXISTS core.app_archive_audit;
            DROP TABLE IF EXISTS core.app_archive;
            DROP TABLE IF EXISTS core.app_migration_run;
            """);
    }
}
