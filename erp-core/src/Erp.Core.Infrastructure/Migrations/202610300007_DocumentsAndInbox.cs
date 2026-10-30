using Erp.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Core.Infrastructure.Migrations;

/// <summary>
/// Document service tables (ADR-0012): files (immutable content, SHA-256, scan status), inboxes and inbox items,
/// and attachments linking a file to any record. Raw SQL, RLS and the standard audit trail on every table.
/// </summary>
[DbContext(typeof(CoreDbContext))]
[Migration("202610300007_DocumentsAndInbox")]
public partial class DocumentsAndInbox : Migration
{
    private static readonly string[] Tables = ["file", "inbox", "inboxitem", "attachment"];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE core.file (
                id uuid PRIMARY KEY,
                tenant_id uuid NOT NULL,
                company_id uuid NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                created_by uuid NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now(),
                updated_by uuid NOT NULL,
                row_version bigint NOT NULL DEFAULT 1,
                file_name character varying(300) NOT NULL,
                content_type character varying(200) NOT NULL,
                size_bytes bigint NOT NULL,
                sha256 character varying(64) NOT NULL,
                storage_key character varying(500) NOT NULL,
                source character varying(16) NOT NULL CHECK (source IN ('upload', 'inbox', 'generated')),
                scan_status character varying(16) NOT NULL DEFAULT 'pending' CHECK (scan_status IN ('pending', 'clean', 'infected')),
                scan_signature character varying(200) NULL,
                scan_attempts integer NOT NULL DEFAULT 0,
                next_scan_at timestamptz NOT NULL DEFAULT now(),
                retain_until timestamptz NULL
            );
            -- Duplicate detection per company: the same content is stored once.
            CREATE UNIQUE INDEX "UX_file_sha256_company"
                ON core.file (tenant_id, COALESCE(company_id, '00000000-0000-0000-0000-000000000000'::uuid), sha256)
                WHERE scan_status <> 'infected';
            CREATE INDEX "IX_file_pending_scan" ON core.file (next_scan_at) WHERE scan_status = 'pending';

            -- Files are immutable: only scan result and retention may change after upload.
            CREATE FUNCTION core.prevent_file_content_change() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.file_name IS DISTINCT FROM OLD.file_name
                   OR NEW.content_type IS DISTINCT FROM OLD.content_type
                   OR NEW.size_bytes IS DISTINCT FROM OLD.size_bytes
                   OR NEW.sha256 IS DISTINCT FROM OLD.sha256
                   OR NEW.storage_key IS DISTINCT FROM OLD.storage_key
                   OR NEW.company_id IS DISTINCT FROM OLD.company_id THEN
                    RAISE EXCEPTION 'core.file content is immutable; store a new file instead.';
                END IF;
                RETURN NEW;
            END;
            $$;
            CREATE TRIGGER trg_file_immutable BEFORE UPDATE ON core.file
                FOR EACH ROW EXECUTE FUNCTION core.prevent_file_content_change();

            CREATE TABLE core.inbox (
                id uuid PRIMARY KEY,
                tenant_id uuid NOT NULL,
                company_id uuid NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                created_by uuid NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now(),
                updated_by uuid NOT NULL,
                row_version bigint NOT NULL DEFAULT 1,
                channel character varying(16) NOT NULL CHECK (channel IN ('email', 'upload', 'api')),
                name character varying(200) NOT NULL,
                address character varying(320) NULL,
                is_active boolean NOT NULL DEFAULT true
            );
            -- The e-mail address of an inbox is unique across all tenants.
            CREATE UNIQUE INDEX "UX_inbox_address" ON core.inbox (lower(address)) WHERE address IS NOT NULL;

            CREATE TABLE core.inboxitem (
                id uuid PRIMARY KEY,
                tenant_id uuid NOT NULL,
                company_id uuid NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                created_by uuid NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now(),
                updated_by uuid NOT NULL,
                row_version bigint NOT NULL DEFAULT 1,
                inbox_id uuid NOT NULL REFERENCES core.inbox (id),
                channel character varying(16) NOT NULL,
                message_id character varying(500) NULL,
                sender character varying(320) NOT NULL DEFAULT '',
                sender_name character varying(300) NULL,
                subject character varying(500) NOT NULL DEFAULT '',
                body_text text NULL,
                received_at timestamptz NOT NULL DEFAULT now(),
                status character varying(16) NOT NULL DEFAULT 'new'
                    CHECK (status IN ('scanning', 'new', 'claimed', 'processed', 'rejected')),
                file_ids uuid[] NOT NULL DEFAULT '{}',
                duplicate_file_ids uuid[] NOT NULL DEFAULT '{}',
                claimed_by character varying(200) NULL,
                claimed_at timestamptz NULL,
                processed_at timestamptz NULL,
                result_object_id character varying(200) NULL,
                result_record_id uuid NULL,
                reject_reason text NULL
            );
            -- A provider that redelivers the same message must not create a second item.
            CREATE UNIQUE INDEX "UX_inboxitem_message" ON core.inboxitem (tenant_id, inbox_id, message_id) WHERE message_id IS NOT NULL;
            CREATE INDEX "IX_inboxitem_status" ON core.inboxitem (tenant_id, status, received_at DESC);

            CREATE TABLE core.attachment (
                id uuid PRIMARY KEY,
                tenant_id uuid NOT NULL,
                company_id uuid NULL,
                created_at timestamptz NOT NULL DEFAULT now(),
                created_by uuid NOT NULL,
                updated_at timestamptz NOT NULL DEFAULT now(),
                updated_by uuid NOT NULL,
                row_version bigint NOT NULL DEFAULT 1,
                file_id uuid NOT NULL REFERENCES core.file (id),
                object_id character varying(200) NOT NULL,
                record_id uuid NOT NULL,
                category character varying(64) NOT NULL,
                retain_until timestamptz NULL,
                UNIQUE (tenant_id, file_id, object_id, record_id, category)
            );
            CREATE INDEX "IX_attachment_record" ON core.attachment (tenant_id, object_id, record_id);
            CREATE INDEX "IX_attachment_file" ON core.attachment (tenant_id, file_id);
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

                GRANT SELECT, INSERT, UPDATE, DELETE ON core."{table}" TO erp_app;

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
            -- Maps the recipient address of an inbound e-mail to its inbox before any tenant context exists.
            CREATE OR REPLACE FUNCTION core.resolve_inbox(p_address text)
            RETURNS TABLE (inbox_id uuid, tenant_id uuid, company_id uuid)
            LANGUAGE sql
            SECURITY DEFINER
            SET search_path = core, pg_temp
            AS $$
                SELECT i.id, i.tenant_id, i.company_id
                  FROM core.inbox i
                 WHERE lower(i.address) = lower(p_address) AND i.is_active AND i.channel = 'email'
                 LIMIT 1;
            $$;

            REVOKE ALL ON FUNCTION core.resolve_inbox(text) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION core.resolve_inbox(text) TO erp_app;

            -- Cross-tenant claim of files still waiting for a virus scan (scanner was unavailable at upload).
            CREATE OR REPLACE FUNCTION core.claim_pending_scans(p_limit integer, p_lease interval DEFAULT interval '60 seconds')
            RETURNS TABLE (file_id uuid, tenant_id uuid)
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path = core, pg_temp
            AS $$
            BEGIN
                RETURN QUERY
                WITH due AS (
                    SELECT f.id
                      FROM core.file f
                     WHERE f.scan_status = 'pending' AND f.next_scan_at <= now()
                     ORDER BY f.next_scan_at
                     LIMIT p_limit
                       FOR UPDATE OF f SKIP LOCKED
                )
                UPDATE core.file u
                   SET next_scan_at = now() + p_lease
                  FROM due
                 WHERE u.id = due.id
                RETURNING u.id, u.tenant_id;
            END;
            $$;

            REVOKE ALL ON FUNCTION core.claim_pending_scans(integer, interval) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION core.claim_pending_scans(integer, interval) TO erp_app;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP FUNCTION IF EXISTS core.claim_pending_scans(integer, interval);
            DROP FUNCTION IF EXISTS core.resolve_inbox(text);
            DROP TABLE IF EXISTS core.attachment_audit;
            DROP TABLE IF EXISTS core.attachment;
            DROP TABLE IF EXISTS core.inboxitem_audit;
            DROP TABLE IF EXISTS core.inboxitem;
            DROP TABLE IF EXISTS core.inbox_audit;
            DROP TABLE IF EXISTS core.inbox;
            DROP TABLE IF EXISTS core.file_audit;
            DROP TABLE IF EXISTS core.file;
            DROP FUNCTION IF EXISTS core.prevent_file_content_change();
            """);
    }
}
