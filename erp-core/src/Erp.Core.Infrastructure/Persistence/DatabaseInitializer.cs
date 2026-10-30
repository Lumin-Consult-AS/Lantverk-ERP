using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Erp.Core.Infrastructure.Persistence;

/// <summary>
/// Handles running EF Core migrations under the migration role and constructing the
/// restricted <c>erp_app</c> connection string for application runtime queries.
/// </summary>
public static class DatabaseInitializer
{
    public const string DefaultAppRole = "erp_app";
    public const string DefaultAppPassword = "erp_app_dev";

    public static async Task MigrateAsync(string migrationConnectionString, CancellationToken cancellationToken = default)
    {
        var optionsBuilder = new DbContextOptionsBuilder<CoreDbContext>();
        optionsBuilder.UseNpgsql(
            migrationConnectionString,
            npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", CoreDbContext.SchemaName));

        await using var migrationDbContext = new CoreDbContext(optionsBuilder.Options);
        await migrationDbContext.Database.MigrateAsync(cancellationToken);
    }

    public static string BuildApplicationConnectionString(
        string connectionString,
        string appRole = DefaultAppRole,
        string appPassword = DefaultAppPassword)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Username = appRole,
            Password = appPassword
        };
        return builder.ConnectionString;
    }
}

