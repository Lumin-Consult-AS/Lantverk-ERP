using Erp.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Erp.Core.Infrastructure.Migrations;

/// <summary>
/// Language and region (ADR-0033). A user may choose a language (<c>language</c>) and a region (<c>region</c>, a country code); a
/// user who has not chosen gets the tenant's defaults (<c>default_language</c>, and the existing <c>default_country_code</c> as
/// region). The core stores the codes only: how a date or a number is written in a region is the client's business.
/// </summary>
[DbContext(typeof(CoreDbContext))]
[Migration("202611010001_LanguageAndRegion")]
public partial class LanguageAndRegion : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE core."user" ADD COLUMN language character varying(8) NULL;
            ALTER TABLE core."user" ADD COLUMN region character varying(2) NULL;
            ALTER TABLE core.tenant ADD COLUMN default_language character varying(8) NOT NULL DEFAULT 'nb';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            ALTER TABLE core.tenant DROP COLUMN IF EXISTS default_language;
            ALTER TABLE core."user" DROP COLUMN IF EXISTS region;
            ALTER TABLE core."user" DROP COLUMN IF EXISTS language;
            """);
    }
}
