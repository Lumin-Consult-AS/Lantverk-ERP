using System;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Core.Infrastructure.Migrations;

[DbContext(typeof(CoreDbContext))]
[Migration("202610300002_MetadataAndAuditInfrastructure")]
public partial class MetadataAndAuditInfrastructure : Migration
{
    private static readonly string[] CoreTables =
    [
        "tenant",
        "country",
        "currency",
        "company",
        "user",
        "metadata_item"
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "metadata_item",
            schema: "core",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                company_id = table.Column<Guid>(type: "uuid", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                created_by = table.Column<Guid>(type: "uuid", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                updated_by = table.Column<Guid>(type: "uuid", nullable: false),
                row_version = table.Column<long>(type: "bigint", nullable: false),
                kind = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                item_id = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                owner_app = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                definition_json = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_metadata_item", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_metadata_item_tenant_id_kind_item_id",
            schema: "core",
            table: "metadata_item",
            columns: new[] { "tenant_id", "kind", "item_id" },
            unique: true);

        migrationBuilder.Sql(
            """
            ALTER TABLE core."metadata_item" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE core."metadata_item" FORCE ROW LEVEL SECURITY;

            CREATE POLICY tenant_isolation ON core."metadata_item"
                USING (tenant_id = current_setting('app.tenant_id')::uuid)
                WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);

            GRANT SELECT, INSERT, UPDATE, DELETE ON core."metadata_item" TO erp_app;
            """);

        // Create trigger functions for append-only audit trail
        migrationBuilder.Sql(
            """
            CREATE OR REPLACE FUNCTION core.prevent_audit_modification()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                RAISE EXCEPTION 'Audit table %.% is append-only and cannot be modified or deleted.', TG_TABLE_SCHEMA, TG_TABLE_NAME
                    USING ERRCODE = '42501';
            END;
            $$;

            CREATE OR REPLACE FUNCTION core.capture_audit_row()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            DECLARE
                v_audit_table text := quote_ident(TG_TABLE_SCHEMA) || '.' || quote_ident(TG_TABLE_NAME || '_audit');
                v_record_id uuid;
                v_tenant_id uuid;
                v_changed_by uuid;
                v_old_data jsonb;
                v_new_data jsonb;
                v_user_setting text;
                v_reason text;
            BEGIN
                v_user_setting := nullif(current_setting('app.user_id', true), '');
                v_reason := nullif(current_setting('app.audit_reason', true), '');

                IF (TG_OP = 'INSERT') THEN
                    v_record_id := NEW.id;
                    v_tenant_id := NEW.tenant_id;
                    IF v_user_setting IS NOT NULL THEN
                        v_changed_by := v_user_setting::uuid;
                    ELSE
                        v_changed_by := COALESCE(NEW.updated_by, NEW.created_by);
                    END IF;
                    v_old_data := NULL;
                    v_new_data := to_jsonb(NEW);
                ELSIF (TG_OP = 'UPDATE') THEN
                    v_record_id := NEW.id;
                    v_tenant_id := NEW.tenant_id;
                    IF v_user_setting IS NOT NULL THEN
                        v_changed_by := v_user_setting::uuid;
                    ELSE
                        v_changed_by := NEW.updated_by;
                    END IF;
                    v_old_data := to_jsonb(OLD);
                    v_new_data := to_jsonb(NEW);
                ELSIF (TG_OP = 'DELETE') THEN
                    v_record_id := OLD.id;
                    v_tenant_id := OLD.tenant_id;
                    IF v_user_setting IS NOT NULL THEN
                        v_changed_by := v_user_setting::uuid;
                    ELSE
                        v_changed_by := COALESCE(OLD.updated_by, OLD.created_by);
                    END IF;
                    v_old_data := to_jsonb(OLD);
                    v_new_data := NULL;
                END IF;

                EXECUTE format(
                    'INSERT INTO %s (record_id, tenant_id, operation, old_data, new_data, changed_by, changed_at, reason) VALUES ($1, $2, $3, $4, $5, $6, now(), $7)',
                    v_audit_table
                ) USING v_record_id, v_tenant_id, TG_OP, v_old_data, v_new_data, v_changed_by, v_reason;

                IF (TG_OP = 'DELETE') THEN
                    RETURN OLD;
                END IF;
                RETURN NEW;
            END;
            $$;
            """);

        foreach (var table in CoreTables)
        {
            migrationBuilder.Sql(
                $"""
                CREATE TABLE IF NOT EXISTS core."{table}_audit" (
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

                CREATE INDEX IF NOT EXISTS "IX_{table}_audit_tenant_record"
                    ON core."{table}_audit" (tenant_id, record_id, audit_id);

                ALTER TABLE core."{table}_audit" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE core."{table}_audit" FORCE ROW LEVEL SECURITY;

                CREATE POLICY tenant_isolation ON core."{table}_audit"
                    USING (tenant_id = current_setting('app.tenant_id')::uuid)
                    WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);

                REVOKE ALL ON core."{table}_audit" FROM PUBLIC, erp_app;
                GRANT SELECT, INSERT ON core."{table}_audit" TO erp_app;
                GRANT USAGE, SELECT ON SEQUENCE core."{table}_audit_audit_id_seq" TO erp_app;

                DROP TRIGGER IF EXISTS "trg_{table}_audit" ON core."{table}";
                CREATE TRIGGER "trg_{table}_audit"
                    AFTER INSERT OR UPDATE OR DELETE ON core."{table}"
                    FOR EACH ROW EXECUTE FUNCTION core.capture_audit_row();

                DROP TRIGGER IF EXISTS "trg_{table}_audit_immutable" ON core."{table}_audit";
                CREATE TRIGGER "trg_{table}_audit_immutable"
                    BEFORE UPDATE OR DELETE OR TRUNCATE ON core."{table}_audit"
                    FOR EACH STATEMENT EXECUTE FUNCTION core.prevent_audit_modification();
                """);
        }
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        foreach (var table in CoreTables)
        {
            migrationBuilder.Sql(
                $"""
                DROP TRIGGER IF EXISTS "trg_{table}_audit" ON core."{table}";
                DROP TABLE IF EXISTS core."{table}_audit";
                """);
        }

        migrationBuilder.DropTable(name: "metadata_item", schema: "core");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS core.capture_audit_row();");
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS core.prevent_audit_modification();");
    }
}

