using System.Data;
using Erp.Core.Domain.Entities;
using Erp.Core.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Erp.Core.Infrastructure.Persistence;

/// <summary>
/// EF Core database context for the core platform schema (<c>core</c>).
/// Tenant isolation is enforced by PostgreSQL Row-Level Security (RLS), not by application-side query filters.
/// </summary>
public sealed class CoreDbContext(
    DbContextOptions<CoreDbContext> options,
    ITenantContext? tenantContext = null) : DbContext(options)
{
    public const string SchemaName = "core";

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<Country> Countries => Set<Country>();
    public DbSet<Currency> Currencies => Set<Currency>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<User> Users => Set<User>();
    public DbSet<MetadataItem> MetadataItems => Set<MetadataItem>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);

        ConfigureTenant(modelBuilder.Entity<Tenant>());
        ConfigureCountry(modelBuilder.Entity<Country>());
        ConfigureCurrency(modelBuilder.Entity<Currency>());
        ConfigureCompany(modelBuilder.Entity<Company>());
        ConfigureUser(modelBuilder.Entity<User>());
        ConfigureMetadataItem(modelBuilder.Entity<MetadataItem>());
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ApplyAuditAndTenancyMetadata();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        ApplyAuditAndTenancyMetadata();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Resolves the tenant ID for a user's OIDC subject (and optional tenant slug) via the
    /// database's minimal SECURITY DEFINER function before tenant session context is established.
    /// </summary>
    public async Task<Guid?> ResolveLoginTenantAsync(
        string externalSubject,
        string? tenantSlug = null,
        CancellationToken cancellationToken = default)
    {
        var connection = Database.GetDbConnection();
        var shouldClose = connection.State != ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "/* erp:set_tenant_context */ SELECT core.resolve_login_tenant(@subject, @slug);";

            var subjectParam = cmd.CreateParameter();
            subjectParam.ParameterName = "subject";
            subjectParam.Value = externalSubject;
            cmd.Parameters.Add(subjectParam);

            var slugParam = cmd.CreateParameter();
            slugParam.ParameterName = "slug";
            slugParam.Value = (object?)tenantSlug ?? DBNull.Value;
            cmd.Parameters.Add(slugParam);

            var scalar = await cmd.ExecuteScalarAsync(cancellationToken);
            return scalar is Guid tenantId ? tenantId : null;
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }

    private void ApplyAuditAndTenancyMetadata()
    {
        var now = DateTimeOffset.UtcNow;
        var currentTenantId = tenantContext?.TenantId;
        var currentUserId = tenantContext?.UserId;

        foreach (var entry in ChangeTracker.Entries<EntityBase>())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.Id == Guid.Empty)
                {
                    entry.Entity.Id = Guid.NewGuid();
                }

                if (entry.Entity is Tenant && entry.Entity.TenantId == Guid.Empty)
                {
                    entry.Entity.TenantId = entry.Entity.Id;
                }
                else if (entry.Entity.TenantId == Guid.Empty && currentTenantId.HasValue)
                {
                    entry.Entity.TenantId = currentTenantId.Value;
                }

                if (entry.Entity.CreatedAt == default)
                {
                    entry.Entity.CreatedAt = now;
                }

                entry.Entity.UpdatedAt = now;

                if (entry.Entity.CreatedBy == Guid.Empty)
                {
                    entry.Entity.CreatedBy = currentUserId ?? entry.Entity.Id;
                }

                if (entry.Entity.UpdatedBy == Guid.Empty)
                {
                    entry.Entity.UpdatedBy = currentUserId ?? entry.Entity.CreatedBy;
                }

                if (entry.Entity.RowVersion <= 0)
                {
                    entry.Entity.RowVersion = 1;
                }
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
                if (currentUserId.HasValue)
                {
                    entry.Entity.UpdatedBy = currentUserId.Value;
                }

                entry.Entity.RowVersion++;
            }
        }
    }

    private static void ConfigureEntityBase<TEntity>(EntityTypeBuilder<TEntity> builder, string tableName)
        where TEntity : EntityBase
    {
        builder.ToTable(tableName, SchemaName);

        builder.HasKey(e => e.Id);

        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .ValueGeneratedNever();

        builder.Property(e => e.TenantId)
            .HasColumnName("tenant_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(e => e.CompanyId)
            .HasColumnName("company_id")
            .HasColumnType("uuid");

        builder.Property(e => e.CreatedAt)
            .HasColumnName("created_at")
            .HasColumnType("timestamptz")
            .IsRequired();

        builder.Property(e => e.CreatedBy)
            .HasColumnName("created_by")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(e => e.UpdatedAt)
            .HasColumnName("updated_at")
            .HasColumnType("timestamptz")
            .IsRequired();

        builder.Property(e => e.UpdatedBy)
            .HasColumnName("updated_by")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(e => e.RowVersion)
            .HasColumnName("row_version")
            .HasColumnType("bigint")
            .IsConcurrencyToken()
            .IsRequired();

        builder.HasIndex(e => e.TenantId);
    }

    private static void ConfigureTenant(EntityTypeBuilder<Tenant> builder)
    {
        ConfigureEntityBase(builder, "tenant");

        builder.Property(e => e.Name)
            .HasColumnName("name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(e => e.Slug)
            .HasColumnName("slug")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(e => e.DefaultCountryCode)
            .HasColumnName("default_country_code")
            .HasMaxLength(2)
            .IsRequired();

        builder.Property(e => e.IsActive)
            .HasColumnName("is_active")
            .IsRequired();

        builder.HasIndex(e => e.Slug).IsUnique();
    }

    private static void ConfigureCountry(EntityTypeBuilder<Country> builder)
    {
        ConfigureEntityBase(builder, "country");

        builder.Property(e => e.IsoCode)
            .HasColumnName("iso_code")
            .HasMaxLength(2)
            .IsRequired();

        builder.Property(e => e.Name)
            .HasColumnName("name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(e => e.DefaultCurrencyCode)
            .HasColumnName("default_currency_code")
            .HasMaxLength(3)
            .IsRequired();

        builder.HasIndex(e => new { e.TenantId, e.IsoCode }).IsUnique();
    }

    private static void ConfigureCurrency(EntityTypeBuilder<Currency> builder)
    {
        ConfigureEntityBase(builder, "currency");

        builder.Property(e => e.IsoCode)
            .HasColumnName("iso_code")
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(e => e.Name)
            .HasColumnName("name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(e => e.Symbol)
            .HasColumnName("symbol")
            .HasMaxLength(10)
            .IsRequired();

        builder.Property(e => e.Decimals)
            .HasColumnName("decimals")
            .IsRequired();

        builder.HasIndex(e => new { e.TenantId, e.IsoCode }).IsUnique();
    }

    private static void ConfigureCompany(EntityTypeBuilder<Company> builder)
    {
        ConfigureEntityBase(builder, "company");

        builder.Property(e => e.Name)
            .HasColumnName("name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(e => e.OrganizationNumber)
            .HasColumnName("organization_number")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(e => e.CountryId)
            .HasColumnName("country_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(e => e.BaseCurrencyId)
            .HasColumnName("base_currency_id")
            .HasColumnType("uuid")
            .IsRequired();

        builder.Property(e => e.IsActive)
            .HasColumnName("is_active")
            .IsRequired();

        builder.HasOne<Country>()
            .WithMany()
            .HasForeignKey(e => e.CountryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Currency>()
            .WithMany()
            .HasForeignKey(e => e.BaseCurrencyId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => new { e.TenantId, e.OrganizationNumber }).IsUnique();
    }

    private static void ConfigureUser(EntityTypeBuilder<User> builder)
    {
        ConfigureEntityBase(builder, "user");

        builder.Property(e => e.ExternalSubject)
            .HasColumnName("external_subject")
            .HasMaxLength(255)
            .IsRequired();

        builder.Property(e => e.Email)
            .HasColumnName("email")
            .HasMaxLength(320)
            .IsRequired();

        builder.Property(e => e.DisplayName)
            .HasColumnName("display_name")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(e => e.Roles)
            .HasColumnName("roles")
            .HasColumnType("text[]")
            .IsRequired();

        builder.Property(e => e.AccessibleCompanyIds)
            .HasColumnName("accessible_company_ids")
            .HasColumnType("uuid[]")
            .IsRequired();

        builder.Property(e => e.IsActive)
            .HasColumnName("is_active")
            .IsRequired();

        builder.HasIndex(e => new { e.TenantId, e.ExternalSubject }).IsUnique();
        builder.HasIndex(e => new { e.TenantId, e.Email }).IsUnique();
    }

    private static void ConfigureMetadataItem(EntityTypeBuilder<MetadataItem> builder)
    {
        ConfigureEntityBase(builder, "metadata_item");

        builder.Property(e => e.Kind)
            .HasColumnName("kind")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(e => e.ItemId)
            .HasColumnName("item_id")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(e => e.OwnerApp)
            .HasColumnName("owner_app")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(e => e.Version)
            .HasColumnName("version")
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(e => e.DefinitionJson)
            .HasColumnName("definition_json")
            .HasColumnType("jsonb")
            .IsRequired();

        builder.HasIndex(e => new { e.TenantId, e.Kind, e.ItemId }).IsUnique();
    }
}
