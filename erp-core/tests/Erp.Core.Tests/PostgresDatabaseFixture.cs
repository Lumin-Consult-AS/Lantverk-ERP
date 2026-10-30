using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;
using Xunit;

namespace Erp.Core.Tests;

[CollectionDefinition(Name)]
public sealed class PostgresDatabaseCollection : ICollectionFixture<PostgresDatabaseFixture>
{
    public const string Name = "PostgresDatabase";
}

/// <summary>
/// Shared Testcontainers PostgreSQL fixture that runs EF Core migrations under the migration user
/// and exposes connections using the non-privileged <c>erp_app</c> role (without BYPASSRLS).
/// </summary>
public sealed class PostgresDatabaseFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("erp_test")
        .WithUsername("erp_migrator")
        .WithPassword("erp_migrator_test")
        .Build();

    public string MigrationConnectionString => _container.GetConnectionString();
    public string ApplicationConnectionString => DatabaseInitializer.BuildApplicationConnectionString(MigrationConnectionString);

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await DatabaseInitializer.MigrateAsync(MigrationConnectionString);

        var tenantContext = new TenantContext();
        await using var appDb = CreateAppDbContext(tenantContext);
        await DemoTenantSeeder.SeedAsync(appDb, tenantContext);
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    public CoreDbContext CreateAppDbContext(ITenantContext tenantContext)
    {
        var interceptor = new TenantSessionInterceptor(tenantContext);
        var options = new DbContextOptionsBuilder<CoreDbContext>()
            .UseNpgsql(
                ApplicationConnectionString,
                npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", CoreDbContext.SchemaName))
            .AddInterceptors(interceptor)
            .Options;

        return new CoreDbContext(options, tenantContext);
    }
}

