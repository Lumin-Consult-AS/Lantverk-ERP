using Erp.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Core.Infrastructure.Migrations;

/// <summary>
/// Immutable records (ADR-0020): the trigger function behind <c>status.immutableIn</c>. A row whose status column holds one
/// of the listed values can be neither updated nor deleted, whoever tries (the application role included).
/// </summary>
[DbContext(typeof(CoreDbContext))]
[Migration("202610310002_ImmutableRecords")]
public partial class ImmutableRecords : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            -- TG_ARGV[0] = the status column, TG_ARGV[1] = comma separated immutable statuses.
            CREATE OR REPLACE FUNCTION core.prevent_immutable_change()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            DECLARE
                v_status text := to_jsonb(OLD) ->> TG_ARGV[0];
            BEGIN
                IF v_status = ANY (string_to_array(TG_ARGV[1], ',')) THEN
                    RAISE EXCEPTION 'immutable record: % in status ''%'' cannot be %d; correct it with a new record',
                        TG_TABLE_NAME, v_status, lower(TG_OP)
                        USING ERRCODE = 'integrity_constraint_violation';
                END IF;

                IF TG_OP = 'DELETE' THEN
                    RETURN OLD;
                END IF;
                RETURN NEW;
            END;
            $$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS core.prevent_immutable_change();");
    }
}
