using Erp.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Core.Infrastructure.Migrations;

/// <summary>
/// Scheduled jobs can be stopped and started by an administrator (<c>enabled</c>), and every run is logged with its
/// outcome and error message (<c>core.app_schedule_run</c>). RLS per tenant, like the schedule state.
/// </summary>
[DbContext(typeof(CoreDbContext))]
[Migration("202610310004_ScheduleControlAndLog")]
public partial class ScheduleControlAndLog : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE core.app_schedule ADD COLUMN enabled boolean NOT NULL DEFAULT true;

            CREATE TABLE core.app_schedule_run (
                id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                tenant_id uuid NOT NULL,
                app_id character varying(200) NOT NULL,
                schedule_id character varying(200) NOT NULL,
                started_at timestamptz NOT NULL,
                finished_at timestamptz NOT NULL,
                trigger character varying(16) NOT NULL,
                status character varying(16) NOT NULL,
                message text NULL
            );
            CREATE INDEX ix_app_schedule_run_job ON core.app_schedule_run (tenant_id, app_id, schedule_id, id DESC);

            ALTER TABLE core.app_schedule_run ENABLE ROW LEVEL SECURITY;
            ALTER TABLE core.app_schedule_run FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON core.app_schedule_run
                USING (tenant_id = current_setting('app.tenant_id')::uuid)
                WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
            REVOKE ALL ON core.app_schedule_run FROM PUBLIC, erp_app;
            GRANT SELECT, INSERT, DELETE ON core.app_schedule_run TO erp_app;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP TABLE IF EXISTS core.app_schedule_run;
            ALTER TABLE core.app_schedule DROP COLUMN IF EXISTS enabled;
            """);
    }
}
