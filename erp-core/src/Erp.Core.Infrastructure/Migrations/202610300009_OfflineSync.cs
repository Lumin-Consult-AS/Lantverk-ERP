using Erp.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Core.Infrastructure.Migrations;

/// <summary>
/// Offline sync (ADR-0014): the change log behind the delta-sync cursor, registered devices, the queue of commands
/// uploaded by offline clients, and reservations. Raw SQL; RLS on all tables; devices are audited.
/// </summary>
[DbContext(typeof(CoreDbContext))]
[Migration("202610300009_OfflineSync")]
public partial class OfflineSync : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            -- Every change to an object table is logged here by a trigger. The cursor of a delta sync is (xid, seq): xid is
            -- the transaction that made the change, so a client is only handed changes of transactions that have finished
            -- (xid < the oldest running transaction). Rows that commit late can then never fall behind a cursor.
            CREATE TABLE core.change_log (
                seq bigserial PRIMARY KEY,
                xid xid8 NOT NULL DEFAULT pg_current_xact_id(),
                tenant_id uuid NOT NULL,
                schema_name character varying(100) NOT NULL,
                table_name character varying(100) NOT NULL,
                record_id uuid NOT NULL,
                operation character(1) NOT NULL CHECK (operation IN ('I', 'U', 'D')),
                changed_at timestamptz NOT NULL DEFAULT now()
            );
            CREATE INDEX "IX_change_log_cursor" ON core.change_log (tenant_id, xid, seq);

            ALTER TABLE core.change_log ENABLE ROW LEVEL SECURITY;
            ALTER TABLE core.change_log FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON core.change_log
                USING (tenant_id = current_setting('app.tenant_id')::uuid)
                WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
            REVOKE ALL ON core.change_log FROM PUBLIC, erp_app;
            GRANT SELECT ON core.change_log TO erp_app;

            -- Only the trigger writes the log (the application role cannot forge or edit entries).
            CREATE OR REPLACE FUNCTION core.capture_change_log()
            RETURNS trigger
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path = core, pg_temp
            AS $$
            BEGIN
                IF TG_OP = 'DELETE' THEN
                    INSERT INTO core.change_log (tenant_id, schema_name, table_name, record_id, operation)
                    VALUES (OLD.tenant_id, TG_TABLE_SCHEMA, TG_TABLE_NAME, OLD.id, 'D');
                    RETURN OLD;
                END IF;

                INSERT INTO core.change_log (tenant_id, schema_name, table_name, record_id, operation)
                VALUES (NEW.tenant_id, TG_TABLE_SCHEMA, TG_TABLE_NAME, NEW.id, CASE TG_OP WHEN 'INSERT' THEN 'I' ELSE 'U' END);
                RETURN NEW;
            END;
            $$;

            -- Entries older than the given age are removed; a device that has been away longer must sync from scratch.
            CREATE OR REPLACE FUNCTION core.purge_change_log(p_older interval DEFAULT interval '90 days')
            RETURNS bigint
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path = core, pg_temp
            AS $$
            DECLARE
                v_deleted bigint;
            BEGIN
                DELETE FROM core.change_log WHERE changed_at < now() - p_older;
                GET DIAGNOSTICS v_deleted = ROW_COUNT;
                RETURN v_deleted;
            END;
            $$;
            REVOKE ALL ON FUNCTION core.purge_change_log(interval) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION core.purge_change_log(interval) TO erp_app;

            CREATE TABLE core.sync_device (
                id uuid PRIMARY KEY,
                tenant_id uuid NOT NULL,
                company_id uuid NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                created_by uuid NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now(),
                updated_by uuid NOT NULL,
                row_version bigint NOT NULL DEFAULT 1,
                user_id uuid NOT NULL,
                name character varying(200) NOT NULL,
                status character varying(16) NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'blocked')),
                last_seen_at timestamptz NULL,
                blocked_at timestamptz NULL,
                blocked_by uuid NULL,
                blocked_reason text NULL
            );

            CREATE TABLE core.sync_command (
                id uuid PRIMARY KEY,
                tenant_id uuid NOT NULL,
                company_id uuid NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                created_by uuid NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now(),
                updated_by uuid NOT NULL,
                row_version bigint NOT NULL DEFAULT 1,
                device_id uuid NOT NULL REFERENCES core.sync_device (id),
                client_command_id uuid NOT NULL,
                command_id character varying(200) NOT NULL,
                args jsonb NOT NULL DEFAULT '{}'::jsonb,
                targets jsonb NOT NULL DEFAULT '[]'::jsonb,
                status character varying(16) NOT NULL
                    CHECK (status IN ('processing', 'applied', 'adjusted', 'rejected', 'conflict', 'manual', 'failed', 'discarded')),
                message text NULL,
                result jsonb NULL,
                conflicts jsonb NULL,
                received_at timestamptz NOT NULL DEFAULT now(),
                processed_at timestamptz NULL,
                UNIQUE (tenant_id, device_id, client_command_id)
            );
            CREATE INDEX "IX_sync_command_status" ON core.sync_command (tenant_id, status, received_at);

            CREATE TABLE core.sync_reservation (
                id uuid PRIMARY KEY,
                tenant_id uuid NOT NULL,
                company_id uuid NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                created_by uuid NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now(),
                updated_by uuid NOT NULL,
                row_version bigint NOT NULL DEFAULT 1,
                device_id uuid NOT NULL REFERENCES core.sync_device (id),
                user_id uuid NOT NULL,
                object_id character varying(200) NOT NULL,
                record_id uuid NOT NULL,
                expires_at timestamptz NOT NULL,
                UNIQUE (tenant_id, object_id, record_id)
            );
            CREATE INDEX "IX_sync_reservation_device" ON core.sync_reservation (tenant_id, device_id);
            """);

        foreach (var table in new[] { "sync_device", "sync_command", "sync_reservation" })
        {
            migrationBuilder.Sql(
                $"""
                ALTER TABLE core."{table}" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE core."{table}" FORCE ROW LEVEL SECURITY;
                CREATE POLICY tenant_isolation ON core."{table}"
                    USING (tenant_id = current_setting('app.tenant_id')::uuid)
                    WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
                GRANT SELECT, INSERT, UPDATE, DELETE ON core."{table}" TO erp_app;
                """);
        }

        migrationBuilder.Sql(
            """
            CREATE TABLE core.sync_device_audit (
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
            CREATE INDEX "IX_sync_device_audit_tenant_record" ON core.sync_device_audit (tenant_id, record_id, audit_id);
            ALTER TABLE core.sync_device_audit ENABLE ROW LEVEL SECURITY;
            ALTER TABLE core.sync_device_audit FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON core.sync_device_audit
                USING (tenant_id = current_setting('app.tenant_id')::uuid)
                WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
            REVOKE ALL ON core.sync_device_audit FROM PUBLIC, erp_app;
            GRANT SELECT, INSERT ON core.sync_device_audit TO erp_app;
            GRANT USAGE, SELECT ON SEQUENCE core.sync_device_audit_audit_id_seq TO erp_app;
            CREATE TRIGGER "trg_sync_device_audit" AFTER INSERT OR UPDATE OR DELETE ON core.sync_device
                FOR EACH ROW EXECUTE FUNCTION core.capture_audit_row();
            CREATE TRIGGER "trg_sync_device_audit_immutable" BEFORE UPDATE OR DELETE OR TRUNCATE ON core.sync_device_audit
                FOR EACH STATEMENT EXECUTE FUNCTION core.prevent_audit_modification();

            -- Objects that already exist get the change-log trigger now; new objects get it from the DDL generator.
            DO $$
            DECLARE
                r record;
                v_schema text;
                v_table text;
            BEGIN
                FOR r IN SELECT DISTINCT item_id FROM core.metadata_item WHERE kind = 'object' LOOP
                    v_schema := lower(regexp_replace(split_part(r.item_id, '.', 1), '([a-z0-9])([A-Z])', '\1_\2', 'g'));
                    v_table := lower(regexp_replace(
                        replace(replace(substr(r.item_id, position('.' in r.item_id) + 1), '.', '_'), '-', '_'),
                        '([a-z0-9])([A-Z])', '\1_\2', 'g'));
                    IF to_regclass(format('%I.%I', v_schema, v_table)) IS NOT NULL THEN
                        EXECUTE format('DROP TRIGGER IF EXISTS %I ON %I.%I', 'trg_' || v_table || '_changelog', v_schema, v_table);
                        EXECUTE format(
                            'CREATE TRIGGER %I AFTER INSERT OR UPDATE OR DELETE ON %I.%I FOR EACH ROW EXECUTE FUNCTION core.capture_change_log()',
                            'trg_' || v_table || '_changelog', v_schema, v_table);
                    END IF;
                END LOOP;
            END $$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP TABLE IF EXISTS core.sync_device_audit;
            DROP TABLE IF EXISTS core.sync_reservation;
            DROP TABLE IF EXISTS core.sync_command;
            DROP TABLE IF EXISTS core.sync_device;
            DROP FUNCTION IF EXISTS core.purge_change_log(interval);
            DROP TABLE IF EXISTS core.change_log;
            DROP FUNCTION IF EXISTS core.capture_change_log();
            """);
    }
}
