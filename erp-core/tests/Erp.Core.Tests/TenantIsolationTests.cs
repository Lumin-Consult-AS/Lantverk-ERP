using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Erp.Core.Tests;

[Collection(PostgresDatabaseCollection.Name)]
public sealed class TenantIsolationTests(PostgresDatabaseFixture fixture)
{
    private static readonly string[] ExpectedCoreTables = ["tenant", "country", "currency", "company", "user"];
    private static readonly string[] MandatoryColumns =
    [
        "id",
        "tenant_id",
        "company_id",
        "created_at",
        "created_by",
        "updated_at",
        "updated_by",
        "row_version"
    ];

    [Fact]
    public async Task ApplicationRole_DoesNotHaveBypassRlsOrSuperuser()
    {
        await using var conn = new NpgsqlConnection(fixture.ApplicationConnectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "SELECT rolsuper, rolbypassrls FROM pg_roles WHERE rolname = current_user;",
            conn);

        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        var isSuper = reader.GetBoolean(0);
        var hasBypassRls = reader.GetBoolean(1);

        Assert.False(isSuper, "erp_app must not be a superuser.");
        Assert.False(hasBypassRls, "erp_app must not have BYPASSRLS.");
    }

    [Fact]
    public async Task AllCoreTables_HaveMandatoryColumnsAndRowLevelSecurityEnabled()
    {
        await using var conn = new NpgsqlConnection(fixture.ApplicationConnectionString);
        await conn.OpenAsync();

        foreach (var table in ExpectedCoreTables)
        {
            await using var colCmd = new NpgsqlCommand(
                """
                SELECT column_name
                  FROM information_schema.columns
                 WHERE table_schema = 'core'
                   AND table_name = @table;
                """,
                conn);
            colCmd.Parameters.AddWithValue("table", table);

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var reader = await colCmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    columns.Add(reader.GetString(0));
                }
            }

            foreach (var mandatory in MandatoryColumns)
            {
                Assert.Contains(mandatory, columns);
            }

            await using var rlsCmd = new NpgsqlCommand(
                """
                SELECT c.relrowsecurity, c.relforcerowsecurity
                  FROM pg_class c
                  JOIN pg_namespace n ON n.oid = c.relnamespace
                 WHERE n.nspname = 'core'
                   AND c.relname = @table;
                """,
                conn);
            rlsCmd.Parameters.AddWithValue("table", table);

            await using var rlsReader = await rlsCmd.ExecuteReaderAsync();
            Assert.True(await rlsReader.ReadAsync(), $"Table core.{table} was not found.");
            Assert.True(rlsReader.GetBoolean(0), $"Row Level Security must be enabled on core.{table}.");
            Assert.True(rlsReader.GetBoolean(1), $"Force Row Level Security must be enabled on core.{table}.");
        }
    }

    [Fact]
    public async Task QueryWithoutTenantFilterInCode_ReturnsOnlyCurrentTenantData()
    {
        var tenantContext = new TenantContext();
        await using var db = fixture.CreateAppDbContext(tenantContext);

        // 1. Query as Tenant 1 (Demo Handel) without any .Where(x => x.TenantId == ...) filter in C#
        using (tenantContext.BeginScope(DemoTenantSeeder.DemoTenantId, DemoTenantSeeder.DemoUserId))
        {
            var tenants = await db.Tenants.AsNoTracking().ToListAsync();
            var companies = await db.Companies.AsNoTracking().ToListAsync();
            var users = await db.Users.AsNoTracking().ToListAsync();
            var countries = await db.Countries.AsNoTracking().ToListAsync();
            var currencies = await db.Currencies.AsNoTracking().ToListAsync();

            var singleTenant = Assert.Single(tenants);
            Assert.Equal(DemoTenantSeeder.DemoTenantId, singleTenant.Id);

            var singleCompany = Assert.Single(companies);
            Assert.Equal(DemoTenantSeeder.DemoTenantId, singleCompany.TenantId);
            Assert.Equal("Demo Handel Norge AS", singleCompany.Name);

            var singleUser = Assert.Single(users);
            Assert.Equal(DemoTenantSeeder.DemoTenantId, singleUser.TenantId);
            Assert.Equal("demo@demohandel.no", singleUser.Email);

            Assert.NotEmpty(countries);
            Assert.All(countries, c => Assert.Equal(DemoTenantSeeder.DemoTenantId, c.TenantId));

            Assert.NotEmpty(currencies);
            Assert.All(currencies, c => Assert.Equal(DemoTenantSeeder.DemoTenantId, c.TenantId));

            // Also verify raw SQL without a WHERE clause only returns Tenant 1 rows
            var rawCompanies = await db.Database
                .SqlQueryRaw<Guid>("SELECT tenant_id AS \"Value\" FROM core.company")
                .ToListAsync();
            Assert.Single(rawCompanies);
            Assert.Equal(DemoTenantSeeder.DemoTenantId, rawCompanies[0]);
        }

        // 2. Switch to Tenant 2 (Nordisk Import) and run the exact same unfiltered queries
        using (tenantContext.BeginScope(DemoTenantSeeder.SecondTenantId, DemoTenantSeeder.SecondUserId))
        {
            var tenants = await db.Tenants.AsNoTracking().ToListAsync();
            var companies = await db.Companies.AsNoTracking().ToListAsync();
            var users = await db.Users.AsNoTracking().ToListAsync();

            var singleTenant = Assert.Single(tenants);
            Assert.Equal(DemoTenantSeeder.SecondTenantId, singleTenant.Id);

            var singleCompany = Assert.Single(companies);
            Assert.Equal(DemoTenantSeeder.SecondTenantId, singleCompany.TenantId);
            Assert.Equal("Nordisk Import AS", singleCompany.Name);

            var singleUser = Assert.Single(users);
            Assert.Equal(DemoTenantSeeder.SecondTenantId, singleUser.TenantId);
            Assert.Equal("anna@nordiskimport.no", singleUser.Email);
        }
    }

    [Fact]
    public async Task QueryWithoutActiveTenantContext_FailsClosed()
    {
        var tenantContext = new TenantContext(); // TenantId is null
        await using var db = fixture.CreateAppDbContext(tenantContext);

        await Assert.ThrowsAsync<PostgresException>(() => db.Companies.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task CrossTenantWriteAttempt_IsRejectedByDatabaseRlsPolicy()
    {
        var tenantContext = new TenantContext();
        await using var db = fixture.CreateAppDbContext(tenantContext);

        using (tenantContext.BeginScope(DemoTenantSeeder.DemoTenantId, DemoTenantSeeder.DemoUserId))
        {
            var country = await db.Countries.AsNoTracking().FirstAsync();
            var currency = await db.Currencies.AsNoTracking().FirstAsync();

            // Attempt to insert a row claiming to belong to SecondTenantId while session is scoped to DemoTenantId
            var ex = await Assert.ThrowsAsync<PostgresException>(() =>
                db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    INSERT INTO core.company (
                        id, tenant_id, company_id, created_at, created_by, updated_at, updated_by, row_version,
                        name, organization_number, country_id, base_currency_id, is_active
                    ) VALUES (
                        {Guid.NewGuid()}, {DemoTenantSeeder.SecondTenantId}, NULL, now(), {DemoTenantSeeder.DemoUserId}, now(), {DemoTenantSeeder.DemoUserId}, 1,
                        'Spoofed Tenant Company', '123123123', {country.Id}, {currency.Id}, true
                    );
                    """));

            Assert.Equal("42501", ex.SqlState); // new row violates row-level security policy
        }
    }
}

