using Erp.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Core.Infrastructure.Migrations;

/// <summary>
/// Number series (ADR-0019): one counter per tenant, company, object field and period. The counter is advanced by
/// <c>core.next_number</c> in the same transaction as the insert of the record that uses it, so a rolled-back create
/// leaves no gap. The application role cannot read or edit the counters directly.
/// </summary>
[DbContext(typeof(CoreDbContext))]
[Migration("202610310001_NumberSeries")]
public partial class NumberSeries : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE TABLE core.number_counter (
                tenant_id uuid NOT NULL,
                company_key uuid NOT NULL,
                object_id character varying(200) NOT NULL,
                field_name character varying(100) NOT NULL,
                period integer NOT NULL,
                last_value bigint NOT NULL,
                PRIMARY KEY (tenant_id, company_key, object_id, field_name, period)
            );

            ALTER TABLE core.number_counter ENABLE ROW LEVEL SECURITY;
            ALTER TABLE core.number_counter FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON core.number_counter
                USING (tenant_id = current_setting('app.tenant_id')::uuid)
                WITH CHECK (tenant_id = current_setting('app.tenant_id')::uuid);
            REVOKE ALL ON core.number_counter FROM PUBLIC, erp_app;

            -- Only this function advances a counter. company_key is the company, or the all-zero uuid for objects that are
            -- not company scoped. period is the year for yearly series and 0 otherwise.
            CREATE OR REPLACE FUNCTION core.next_number(p_company uuid, p_object text, p_field text, p_period integer)
            RETURNS bigint
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path = core, pg_temp
            AS $$
            DECLARE
                v_value bigint;
            BEGIN
                INSERT INTO core.number_counter AS c (tenant_id, company_key, object_id, field_name, period, last_value)
                VALUES (current_setting('app.tenant_id')::uuid,
                        COALESCE(p_company, '00000000-0000-0000-0000-000000000000'::uuid),
                        p_object, p_field, p_period, 1)
                ON CONFLICT (tenant_id, company_key, object_id, field_name, period)
                DO UPDATE SET last_value = c.last_value + 1
                RETURNING c.last_value INTO v_value;
                RETURN v_value;
            END;
            $$;
            REVOKE ALL ON FUNCTION core.next_number(uuid, text, text, integer) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION core.next_number(uuid, text, text, integer) TO erp_app;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP FUNCTION IF EXISTS core.next_number(uuid, text, text, integer);
            DROP TABLE IF EXISTS core.number_counter;
            """);
    }
}
