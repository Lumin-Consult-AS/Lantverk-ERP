using Erp.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Core.Infrastructure.Migrations;

/// <summary>
/// Transactional outbox for the event bus (ADR-0009). Tables are created with plain SQL and are
/// accessed with raw SQL, so they are intentionally not part of the EF model snapshot.
/// </summary>
[DbContext(typeof(CoreDbContext))]
[Migration("202610300004_EventBus")]
public partial class EventBus : Migration
{
    private static readonly string[] Tables =
    [
        "event_log",
        "event_delivery",
        "webhook_subscription",
        "tenant_secret"
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE core.event_log (
                id uuid PRIMARY KEY,
                tenant_id uuid NOT NULL,
                company_id uuid NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                created_by uuid NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now(),
                updated_by uuid NOT NULL,
                row_version bigint NOT NULL DEFAULT 1,
                event_name character varying(256) NOT NULL,
                aggregate_id character varying(128) NOT NULL,
                correlation_id character varying(64) NOT NULL,
                occurred_at timestamptz NOT NULL,
                payload jsonb NOT NULL
            );
            CREATE INDEX "IX_event_log_tenant_occurred" ON core.event_log (tenant_id, occurred_at DESC);
            CREATE INDEX "IX_event_log_occurred" ON core.event_log (occurred_at);

            CREATE TABLE core.event_delivery (
                id uuid PRIMARY KEY,
                tenant_id uuid NOT NULL,
                company_id uuid NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                created_by uuid NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now(),
                updated_by uuid NOT NULL,
                row_version bigint NOT NULL DEFAULT 1,
                event_id uuid NOT NULL REFERENCES core.event_log (id) ON DELETE CASCADE,
                seq bigserial NOT NULL,
                subscriber_type character varying(16) NOT NULL CHECK (subscriber_type IN ('app', 'webhook')),
                subscriber_key character varying(300) NOT NULL,
                aggregate_id character varying(128) NOT NULL,
                status character varying(16) NOT NULL DEFAULT 'pending' CHECK (status IN ('pending', 'delivered', 'dead')),
                attempts integer NOT NULL DEFAULT 0,
                next_attempt_at timestamptz NOT NULL DEFAULT now(),
                last_error text NULL,
                delivered_at timestamptz NULL,
                is_replay boolean NOT NULL DEFAULT false
            );
            CREATE INDEX "IX_event_delivery_due" ON core.event_delivery (next_attempt_at) WHERE status = 'pending';
            CREATE INDEX "IX_event_delivery_event" ON core.event_delivery (tenant_id, event_id);
            CREATE INDEX "IX_event_delivery_ordering"
                ON core.event_delivery (tenant_id, subscriber_key, aggregate_id, seq) WHERE status = 'pending';

            CREATE TABLE core.webhook_subscription (
                id uuid PRIMARY KEY,
                tenant_id uuid NOT NULL,
                company_id uuid NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                created_by uuid NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now(),
                updated_by uuid NOT NULL,
                row_version bigint NOT NULL DEFAULT 1,
                url character varying(1000) NOT NULL,
                event_pattern character varying(256) NOT NULL DEFAULT '*',
                is_active boolean NOT NULL DEFAULT true
            );

            CREATE TABLE core.tenant_secret (
                id uuid PRIMARY KEY,
                tenant_id uuid NOT NULL,
                company_id uuid NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                created_by uuid NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now(),
                updated_by uuid NOT NULL,
                row_version bigint NOT NULL DEFAULT 1,
                name character varying(64) NOT NULL,
                secret text NOT NULL,
                UNIQUE (tenant_id, name)
            );
            """);

        foreach (var table in Tables)
        {
            migrationBuilder.Sql(
                $"""
                ALTER TABLE core."{table}" ENABLE ROW LEVEL SECURITY;
                ALTER TABLE core."{table}" FORCE ROW LEVEL SECURITY;

                CREATE POLICY tenant_isolation ON core."{table}"
                    USING (tenant_id = current_setting('app.tenant_id')::uuid)
                    WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
                """);
        }

        migrationBuilder.Sql(
            """
            -- The event log is append-only for the application role.
            REVOKE ALL ON core.event_log FROM PUBLIC, erp_app;
            GRANT SELECT, INSERT ON core.event_log TO erp_app;

            GRANT SELECT, INSERT, UPDATE, DELETE ON core.event_delivery TO erp_app;
            GRANT USAGE, SELECT ON SEQUENCE core.event_delivery_seq_seq TO erp_app;
            GRANT SELECT, INSERT, UPDATE, DELETE ON core.webhook_subscription TO erp_app;
            GRANT SELECT, INSERT ON core.tenant_secret TO erp_app;

            -- Cross-tenant claim of due deliveries for the delivery worker. Returns only identifiers;
            -- the delivery itself is then processed under the tenant's RLS context.
            CREATE OR REPLACE FUNCTION core.claim_due_deliveries(
                p_limit integer,
                p_lease interval DEFAULT interval '60 seconds'
            )
            RETURNS TABLE (delivery_id uuid, tenant_id uuid)
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path = core, pg_temp
            AS $$
            BEGIN
                RETURN QUERY
                WITH due AS (
                    SELECT d.id
                      FROM core.event_delivery d
                     WHERE d.status = 'pending'
                       AND d.next_attempt_at <= now()
                       AND (d.is_replay OR NOT EXISTS (
                            SELECT 1
                              FROM core.event_delivery p
                             WHERE p.tenant_id = d.tenant_id
                               AND p.subscriber_key = d.subscriber_key
                               AND p.aggregate_id = d.aggregate_id
                               AND p.status = 'pending'
                               AND NOT p.is_replay
                               AND p.seq < d.seq))
                     ORDER BY d.seq
                     LIMIT p_limit
                       FOR UPDATE OF d SKIP LOCKED
                )
                UPDATE core.event_delivery u
                   SET next_attempt_at = now() + p_lease
                  FROM due
                 WHERE u.id = due.id
                RETURNING u.id, u.tenant_id;
            END;
            $$;
            """);

        migrationBuilder.Sql(
            """
            REVOKE ALL ON FUNCTION core.claim_due_deliveries(integer, interval) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION core.claim_due_deliveries(integer, interval) TO erp_app;

            -- erp_app has no DELETE on core.event_log; retention is enforced by this function.
            CREATE OR REPLACE FUNCTION core.purge_expired_events(
                p_retention interval DEFAULT interval '90 days'
            )
            RETURNS integer
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path = core, pg_temp
            AS $$
            DECLARE
                v_deleted integer;
            BEGIN
                DELETE FROM core.event_log WHERE occurred_at < now() - p_retention;
                GET DIAGNOSTICS v_deleted = ROW_COUNT;
                RETURN v_deleted;
            END;
            $$;

            REVOKE ALL ON FUNCTION core.purge_expired_events(interval) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION core.purge_expired_events(interval) TO erp_app;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP FUNCTION IF EXISTS core.purge_expired_events(interval);
            DROP FUNCTION IF EXISTS core.claim_due_deliveries(integer, interval);
            DROP TABLE IF EXISTS core.tenant_secret;
            DROP TABLE IF EXISTS core.webhook_subscription;
            DROP TABLE IF EXISTS core.event_delivery;
            DROP TABLE IF EXISTS core.event_log;
            """);
    }
}
