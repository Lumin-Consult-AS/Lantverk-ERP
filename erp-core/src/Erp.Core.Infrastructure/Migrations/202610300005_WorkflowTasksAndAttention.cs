using Erp.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Core.Infrastructure.Migrations;

/// <summary>
/// Workflow definitions, running instances and tasks (ADR-0010), plus per-user hide/snooze of attention items.
/// Accessed with raw SQL, so intentionally not part of the EF model snapshot. Definitions, instances and tasks
/// get the standard append-only audit trail.
/// </summary>
[DbContext(typeof(CoreDbContext))]
[Migration("202610300005_WorkflowTasksAndAttention")]
public partial class WorkflowTasksAndAttention : Migration
{
    private static readonly string[] AuditedTables =
    [
        "workflow_definition",
        "workflow_instance",
        "task"
    ];

    private static readonly string[] AllTables = [.. AuditedTables, "attention_dismissal"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE core.workflow_definition (
                id uuid PRIMARY KEY,
                tenant_id uuid NOT NULL,
                company_id uuid NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                created_by uuid NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now(),
                updated_by uuid NOT NULL,
                row_version bigint NOT NULL DEFAULT 1,
                workflow_id character varying(200) NOT NULL,
                object_id character varying(200) NOT NULL,
                definition jsonb NOT NULL,
                source_app character varying(128) NULL,
                is_customized boolean NOT NULL DEFAULT false,
                UNIQUE (tenant_id, workflow_id)
            );

            CREATE TABLE core.workflow_instance (
                id uuid PRIMARY KEY,
                tenant_id uuid NOT NULL,
                company_id uuid NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                created_by uuid NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now(),
                updated_by uuid NOT NULL,
                row_version bigint NOT NULL DEFAULT 1,
                workflow_id character varying(200) NOT NULL,
                definition jsonb NOT NULL,
                object_id character varying(200) NOT NULL,
                record_id uuid NOT NULL,
                transition character varying(128) NOT NULL,
                status character varying(16) NOT NULL DEFAULT 'running' CHECK (status IN ('running', 'completed', 'rejected')),
                completed_at timestamptz NULL
            );
            CREATE INDEX "IX_workflow_instance_record" ON core.workflow_instance (tenant_id, object_id, record_id);
            CREATE UNIQUE INDEX "UX_workflow_instance_running"
                ON core.workflow_instance (tenant_id, object_id, record_id) WHERE status = 'running';

            CREATE TABLE core.task (
                id uuid PRIMARY KEY,
                tenant_id uuid NOT NULL,
                company_id uuid NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                created_by uuid NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now(),
                updated_by uuid NOT NULL,
                row_version bigint NOT NULL DEFAULT 1,
                workflow_instance_id uuid NOT NULL REFERENCES core.workflow_instance (id),
                step_index integer NOT NULL,
                title character varying(300) NOT NULL,
                object_id character varying(200) NOT NULL,
                record_id uuid NOT NULL,
                link character varying(500) NOT NULL,
                assignee_user uuid NULL,
                assignee_role character varying(200) NULL,
                status character varying(16) NOT NULL DEFAULT 'open'
                    CHECK (status IN ('waiting', 'open', 'approved', 'rejected', 'cancelled')),
                due_at timestamptz NULL,
                escalated_at timestamptz NULL,
                delegated_from uuid NULL,
                completed_by uuid NULL,
                completed_at timestamptz NULL,
                comment text NULL,
                notified_at timestamptz NULL,
                CHECK (assignee_user IS NOT NULL OR assignee_role IS NOT NULL)
            );
            CREATE INDEX "IX_task_instance" ON core.task (tenant_id, workflow_instance_id, step_index);
            CREATE INDEX "IX_task_open" ON core.task (tenant_id, status) WHERE status = 'open';
            CREATE INDEX "IX_task_due" ON core.task (due_at) WHERE status = 'open' AND escalated_at IS NULL;

            CREATE TABLE core.attention_dismissal (
                id uuid PRIMARY KEY,
                tenant_id uuid NOT NULL,
                company_id uuid NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                created_by uuid NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now(),
                updated_by uuid NOT NULL,
                row_version bigint NOT NULL DEFAULT 1,
                user_id uuid NOT NULL,
                attention_id character varying(200) NOT NULL,
                snoozed_until timestamptz NULL,
                UNIQUE (tenant_id, user_id, attention_id)
            );
            """);

        foreach (var table in AllTables)
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

        foreach (var table in AuditedTables)
        {
            migrationBuilder.Sql(
                $"""
                CREATE TABLE core."{table}_audit" (
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

                CREATE INDEX "IX_{table}_audit_tenant_record"
                    ON core."{table}_audit" (tenant_id, record_id, audit_id);

                ALTER TABLE core."{table}_audit" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE core."{table}_audit" FORCE ROW LEVEL SECURITY;

                CREATE POLICY tenant_isolation ON core."{table}_audit"
                    USING (tenant_id = current_setting('app.tenant_id')::uuid)
                    WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);

                REVOKE ALL ON core."{table}_audit" FROM PUBLIC, erp_app;
                GRANT SELECT, INSERT ON core."{table}_audit" TO erp_app;
                GRANT USAGE, SELECT ON SEQUENCE core."{table}_audit_audit_id_seq" TO erp_app;

                CREATE TRIGGER "trg_{table}_audit"
                    AFTER INSERT OR UPDATE OR DELETE ON core."{table}"
                    FOR EACH ROW EXECUTE FUNCTION core.capture_audit_row();

                CREATE TRIGGER "trg_{table}_audit_immutable"
                    BEFORE UPDATE OR DELETE OR TRUNCATE ON core."{table}_audit"
                    FOR EACH STATEMENT EXECUTE FUNCTION core.prevent_audit_modification();
                """);
        }

        migrationBuilder.Sql(
            """
            -- Cross-tenant claim of overdue tasks for the escalation worker. Marks the task as escalated so it is
            -- claimed once; the worker then reassigns it under the tenant's own RLS context.
            CREATE OR REPLACE FUNCTION core.claim_overdue_tasks(p_limit integer)
            RETURNS TABLE (task_id uuid, tenant_id uuid)
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path = core, pg_temp
            AS $$
            BEGIN
                RETURN QUERY
                WITH due AS (
                    SELECT t.id
                      FROM core.task t
                     WHERE t.status = 'open' AND t.due_at <= now() AND t.escalated_at IS NULL
                     ORDER BY t.due_at
                     LIMIT p_limit
                       FOR UPDATE OF t SKIP LOCKED
                )
                UPDATE core.task u
                   SET escalated_at = now()
                  FROM due
                 WHERE u.id = due.id
                RETURNING u.id, u.tenant_id;
            END;
            $$;

            REVOKE ALL ON FUNCTION core.claim_overdue_tasks(integer) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION core.claim_overdue_tasks(integer) TO erp_app;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP FUNCTION IF EXISTS core.claim_overdue_tasks(integer);
            DROP TABLE IF EXISTS core.attention_dismissal;
            DROP TABLE IF EXISTS core.task_audit;
            DROP TABLE IF EXISTS core.task;
            DROP TABLE IF EXISTS core.workflow_instance_audit;
            DROP TABLE IF EXISTS core.workflow_instance;
            DROP TABLE IF EXISTS core.workflow_definition_audit;
            DROP TABLE IF EXISTS core.workflow_definition;
            """);
    }
}
