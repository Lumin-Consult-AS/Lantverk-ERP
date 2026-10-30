using Erp.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Core.Infrastructure.Migrations;

/// <summary>
/// A tenant's roles are composed of permission sets defined by installed apps (ADR-0011). Users carry role names
/// (<c>core.user.roles</c>); this table says which permission sets each role holds. Raw SQL, audited.
/// </summary>
[DbContext(typeof(CoreDbContext))]
[Migration("202610300006_RolePermissionSets")]
public partial class RolePermissionSets : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE core.role_permission_set (
                id uuid PRIMARY KEY,
                tenant_id uuid NOT NULL,
                company_id uuid NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                created_by uuid NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now(),
                updated_by uuid NOT NULL,
                row_version bigint NOT NULL DEFAULT 1,
                role character varying(200) NOT NULL,
                permission_set_id character varying(200) NOT NULL,
                UNIQUE (tenant_id, role, permission_set_id)
            );

            ALTER TABLE core.role_permission_set ENABLE ROW LEVEL SECURITY;
            ALTER TABLE core.role_permission_set FORCE ROW LEVEL SECURITY;

            CREATE POLICY tenant_isolation ON core.role_permission_set
                USING (tenant_id = current_setting('app.tenant_id')::uuid)
                WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);

            GRANT SELECT, INSERT, UPDATE, DELETE ON core.role_permission_set TO erp_app;

            CREATE TABLE core.role_permission_set_audit (
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

            CREATE INDEX "IX_role_permission_set_audit_tenant_record"
                ON core.role_permission_set_audit (tenant_id, record_id, audit_id);

            ALTER TABLE core.role_permission_set_audit ENABLE ROW LEVEL SECURITY;
            ALTER TABLE core.role_permission_set_audit FORCE ROW LEVEL SECURITY;

            CREATE POLICY tenant_isolation ON core.role_permission_set_audit
                USING (tenant_id = current_setting('app.tenant_id')::uuid)
                WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);

            REVOKE ALL ON core.role_permission_set_audit FROM PUBLIC, erp_app;
            GRANT SELECT, INSERT ON core.role_permission_set_audit TO erp_app;
            GRANT USAGE, SELECT ON SEQUENCE core.role_permission_set_audit_audit_id_seq TO erp_app;

            CREATE TRIGGER "trg_role_permission_set_audit"
                AFTER INSERT OR UPDATE OR DELETE ON core.role_permission_set
                FOR EACH ROW EXECUTE FUNCTION core.capture_audit_row();

            CREATE TRIGGER "trg_role_permission_set_audit_immutable"
                BEFORE UPDATE OR DELETE OR TRUNCATE ON core.role_permission_set_audit
                FOR EACH STATEMENT EXECUTE FUNCTION core.prevent_audit_modification();
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP TABLE IF EXISTS core.role_permission_set_audit;
            DROP TABLE IF EXISTS core.role_permission_set;
            """);
    }
}
