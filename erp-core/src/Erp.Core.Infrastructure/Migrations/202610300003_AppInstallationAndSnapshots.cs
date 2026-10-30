using System;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Core.Infrastructure.Migrations;

[DbContext(typeof(CoreDbContext))]
[Migration("202610300003_AppInstallationAndSnapshots")]
public partial class AppInstallationAndSnapshots : Migration
{
    private static readonly string[] NewTables =
    [
        "installed_app",
        "metadata_snapshot"
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "installed_app",
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
                app_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                publisher = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                is_declarative = table.Column<bool>(type: "boolean", nullable: false),
                container_image = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                package_sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                package_storage_key = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                manifest_json = table.Column<string>(type: "jsonb", nullable: false),
                pages_json = table.Column<string>(type: "jsonb", nullable: false),
                permission_sets_json = table.Column<string>(type: "jsonb", nullable: false),
                translations_json = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_installed_app", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_installed_app_tenant_id_app_id",
            schema: "core",
            table: "installed_app",
            columns: new[] { "tenant_id", "app_id" },
            unique: true);

        migrationBuilder.CreateTable(
            name: "metadata_snapshot",
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
                snapshot_number = table.Column<int>(type: "integer", nullable: false),
                trigger_action = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                trigger_app_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                trigger_app_version = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                snapshot_json = table.Column<string>(type: "jsonb", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_metadata_snapshot", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_metadata_snapshot_tenant_id_snapshot_number",
            schema: "core",
            table: "metadata_snapshot",
            columns: new[] { "tenant_id", "snapshot_number" },
            unique: true);

        foreach (var table in NewTables)
        {
            migrationBuilder.Sql(
                $"""
                ALTER TABLE core."{table}" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE core."{table}" FORCE ROW LEVEL SECURITY;

                CREATE POLICY tenant_isolation ON core."{table}"
                    USING (tenant_id = current_setting('app.tenant_id')::uuid)
                    WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);

                GRANT SELECT, INSERT, UPDATE, DELETE ON core."{table}" TO erp_app;

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
        foreach (var table in NewTables)
        {
            migrationBuilder.Sql(
                $"""
                DROP TRIGGER IF EXISTS "trg_{table}_audit" ON core."{table}";
                DROP TABLE IF EXISTS core."{table}_audit";
                """);
        }

        migrationBuilder.DropTable(name: "metadata_snapshot", schema: "core");
        migrationBuilder.DropTable(name: "installed_app", schema: "core");
    }
}

