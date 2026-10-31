using Erp.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Core.Infrastructure.Migrations;

/// <summary>
/// Scheduled app commands (ADR-0025): when each schedule of each installed app last ran and when it is due next.
/// The definitions themselves are in the installed app's manifest; this holds only the state. RLS per tenant.
/// </summary>
[DbContext(typeof(CoreDbContext))]
[Migration("202610310003_AppSchedules")]
public partial class AppSchedules : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE core.app_schedule (
                tenant_id uuid NOT NULL,
                app_id character varying(200) NOT NULL,
                schedule_id character varying(200) NOT NULL,
                next_run_at timestamptz NOT NULL,
                last_run_at timestamptz NULL,
                last_status character varying(16) NULL,
                last_message text NULL,
                PRIMARY KEY (tenant_id, app_id, schedule_id)
            );

            ALTER TABLE core.app_schedule ENABLE ROW LEVEL SECURITY;
            ALTER TABLE core.app_schedule FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON core.app_schedule
                USING (tenant_id = current_setting('app.tenant_id')::uuid)
                WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
            REVOKE ALL ON core.app_schedule FROM PUBLIC, erp_app;
            GRANT SELECT, INSERT, UPDATE, DELETE ON core.app_schedule TO erp_app;

            -- Which tenants have an installed app with schedules: the worker has no tenant until it knows.
            CREATE OR REPLACE FUNCTION core.tenants_with_schedules()
            RETURNS TABLE (tenant_id uuid)
            LANGUAGE sql
            SECURITY DEFINER
            SET search_path = core, pg_temp
            AS $$
                SELECT DISTINCT a.tenant_id
                  FROM core.installed_app a
                 WHERE jsonb_array_length(COALESCE(a.manifest_json -> 'schedules', '[]'::jsonb)) > 0;
            $$;
            REVOKE ALL ON FUNCTION core.tenants_with_schedules() FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION core.tenants_with_schedules() TO erp_app;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP FUNCTION IF EXISTS core.tenants_with_schedules();
            DROP TABLE IF EXISTS core.app_schedule;
            """);
    }
}
