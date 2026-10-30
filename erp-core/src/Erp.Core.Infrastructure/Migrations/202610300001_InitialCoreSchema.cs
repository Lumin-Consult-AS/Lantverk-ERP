using System;
using System.Collections.Generic;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Core.Infrastructure.Migrations;

[DbContext(typeof(CoreDbContext))]
[Migration("202610300001_InitialCoreSchema")]
public partial class InitialCoreSchema : Migration
{
    private static readonly string[] CoreTables =
    [
        "tenant",
        "country",
        "currency",
        "company",
        "user"
    ];

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema(name: "core");

        migrationBuilder.CreateTable(
            name: "tenant",
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
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                default_country_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_tenant", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "country",
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
                iso_code = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                default_currency_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_country", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "currency",
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
                iso_code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                symbol = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                decimals = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_currency", x => x.id);
            });

        migrationBuilder.CreateTable(
            name: "company",
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
                name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                organization_number = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                country_id = table.Column<Guid>(type: "uuid", nullable: false),
                base_currency_id = table.Column<Guid>(type: "uuid", nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_company", x => x.id);
                table.ForeignKey(
                    name: "FK_company_country_country_id",
                    column: x => x.country_id,
                    principalSchema: "core",
                    principalTable: "country",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_company_currency_base_currency_id",
                    column: x => x.base_currency_id,
                    principalSchema: "core",
                    principalTable: "currency",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "user",
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
                external_subject = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                display_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                roles = table.Column<List<string>>(type: "text[]", nullable: false),
                accessible_company_ids = table.Column<List<Guid>>(type: "uuid[]", nullable: false),
                is_active = table.Column<bool>(type: "boolean", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_user", x => x.id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_tenant_slug",
            schema: "core",
            table: "tenant",
            column: "slug",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_tenant_tenant_id",
            schema: "core",
            table: "tenant",
            column: "tenant_id");

        migrationBuilder.CreateIndex(
            name: "IX_country_tenant_id",
            schema: "core",
            table: "country",
            column: "tenant_id");

        migrationBuilder.CreateIndex(
            name: "IX_country_tenant_id_iso_code",
            schema: "core",
            table: "country",
            columns: new[] { "tenant_id", "iso_code" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_currency_tenant_id",
            schema: "core",
            table: "currency",
            column: "tenant_id");

        migrationBuilder.CreateIndex(
            name: "IX_currency_tenant_id_iso_code",
            schema: "core",
            table: "currency",
            columns: new[] { "tenant_id", "iso_code" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_company_base_currency_id",
            schema: "core",
            table: "company",
            column: "base_currency_id");

        migrationBuilder.CreateIndex(
            name: "IX_company_country_id",
            schema: "core",
            table: "company",
            column: "country_id");

        migrationBuilder.CreateIndex(
            name: "IX_company_tenant_id",
            schema: "core",
            table: "company",
            column: "tenant_id");

        migrationBuilder.CreateIndex(
            name: "IX_company_tenant_id_organization_number",
            schema: "core",
            table: "company",
            columns: new[] { "tenant_id", "organization_number" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_user_tenant_id",
            schema: "core",
            table: "user",
            column: "tenant_id");

        migrationBuilder.CreateIndex(
            name: "IX_user_tenant_id_email",
            schema: "core",
            table: "user",
            columns: new[] { "tenant_id", "email" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_user_tenant_id_external_subject",
            schema: "core",
            table: "user",
            columns: new[] { "tenant_id", "external_subject" },
            unique: true);

        // Create dedicated application role without BYPASSRLS and grant privileges on core schema.
        migrationBuilder.Sql(
            """
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'erp_app') THEN
                    CREATE ROLE erp_app LOGIN PASSWORD 'erp_app_dev' NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
                ELSE
                    ALTER ROLE erp_app NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS;
                END IF;
            END
            $$;

            GRANT USAGE ON SCHEMA core TO erp_app;
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA core TO erp_app;
            ALTER DEFAULT PRIVILEGES IN SCHEMA core GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO erp_app;
            """);

        // Enable Row-Level Security and create tenant_isolation policy on every core table.
        foreach (var table in CoreTables)
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

        // Create minimal SECURITY DEFINER function to resolve a user's tenant_id during OIDC login.
        migrationBuilder.Sql(
            """
            CREATE OR REPLACE FUNCTION core.resolve_login_tenant(
                p_external_subject text,
                p_tenant_slug text DEFAULT NULL
            )
            RETURNS uuid
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path = core, pg_temp
            AS $$
            DECLARE
                v_tenant_id uuid;
            BEGIN
                SELECT u.tenant_id
                  INTO v_tenant_id
                  FROM core."user" u
                  JOIN core.tenant t ON t.id = u.tenant_id
                 WHERE u.external_subject = p_external_subject
                   AND u.is_active = true
                   AND t.is_active = true
                   AND (p_tenant_slug IS NULL OR t.slug = p_tenant_slug)
                 ORDER BY u.created_at ASC
                 LIMIT 1;

                RETURN v_tenant_id;
            END;
            $$;

            REVOKE ALL ON FUNCTION core.resolve_login_tenant(text, text) FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION core.resolve_login_tenant(text, text) TO erp_app;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP FUNCTION IF EXISTS core.resolve_login_tenant(text, text);");
        migrationBuilder.DropTable(name: "company", schema: "core");
        migrationBuilder.DropTable(name: "user", schema: "core");
        migrationBuilder.DropTable(name: "country", schema: "core");
        migrationBuilder.DropTable(name: "currency", schema: "core");
        migrationBuilder.DropTable(name: "tenant", schema: "core");
    }
}

