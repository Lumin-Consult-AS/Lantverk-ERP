using System;
using System.Collections.Generic;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Erp.Core.Infrastructure.Migrations;

[DbContext(typeof(CoreDbContext))]
partial class CoreDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
#pragma warning disable 612, 618
        modelBuilder
            .HasDefaultSchema("core")
            .HasAnnotation("ProductVersion", "10.0.0")
            .HasAnnotation("Relational:MaxIdentifierLength", 63);

        NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

        modelBuilder.Entity("Erp.Core.Domain.Entities.Company", b =>
        {
            b.Property<Guid>("Id").HasColumnType("uuid").HasColumnName("id");
            b.Property<Guid>("BaseCurrencyId").HasColumnType("uuid").HasColumnName("base_currency_id");
            b.Property<Guid?>("CompanyId").HasColumnType("uuid").HasColumnName("company_id");
            b.Property<Guid>("CountryId").HasColumnType("uuid").HasColumnName("country_id");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamptz").HasColumnName("created_at");
            b.Property<Guid>("CreatedBy").HasColumnType("uuid").HasColumnName("created_by");
            b.Property<bool>("IsActive").HasColumnType("boolean").HasColumnName("is_active");
            b.Property<string>("Name").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)").HasColumnName("name");
            b.Property<string>("OrganizationNumber").IsRequired().HasMaxLength(50).HasColumnType("character varying(50)").HasColumnName("organization_number");
            b.Property<long>("RowVersion").IsConcurrencyToken().HasColumnType("bigint").HasColumnName("row_version");
            b.Property<Guid>("TenantId").HasColumnType("uuid").HasColumnName("tenant_id");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamptz").HasColumnName("updated_at");
            b.Property<Guid>("UpdatedBy").HasColumnType("uuid").HasColumnName("updated_by");

            b.HasKey("Id");
            b.HasIndex("BaseCurrencyId");
            b.HasIndex("CountryId");
            b.HasIndex("TenantId");
            b.HasIndex("TenantId", "OrganizationNumber").IsUnique();
            b.ToTable("company", "core");
        });

        modelBuilder.Entity("Erp.Core.Domain.Entities.Country", b =>
        {
            b.Property<Guid>("Id").HasColumnType("uuid").HasColumnName("id");
            b.Property<Guid?>("CompanyId").HasColumnType("uuid").HasColumnName("company_id");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamptz").HasColumnName("created_at");
            b.Property<Guid>("CreatedBy").HasColumnType("uuid").HasColumnName("created_by");
            b.Property<string>("DefaultCurrencyCode").IsRequired().HasMaxLength(3).HasColumnType("character varying(3)").HasColumnName("default_currency_code");
            b.Property<string>("IsoCode").IsRequired().HasMaxLength(2).HasColumnType("character varying(2)").HasColumnName("iso_code");
            b.Property<string>("Name").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)").HasColumnName("name");
            b.Property<long>("RowVersion").IsConcurrencyToken().HasColumnType("bigint").HasColumnName("row_version");
            b.Property<Guid>("TenantId").HasColumnType("uuid").HasColumnName("tenant_id");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamptz").HasColumnName("updated_at");
            b.Property<Guid>("UpdatedBy").HasColumnType("uuid").HasColumnName("updated_by");

            b.HasKey("Id");
            b.HasIndex("TenantId");
            b.HasIndex("TenantId", "IsoCode").IsUnique();
            b.ToTable("country", "core");
        });

        modelBuilder.Entity("Erp.Core.Domain.Entities.Currency", b =>
        {
            b.Property<Guid>("Id").HasColumnType("uuid").HasColumnName("id");
            b.Property<Guid?>("CompanyId").HasColumnType("uuid").HasColumnName("company_id");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamptz").HasColumnName("created_at");
            b.Property<Guid>("CreatedBy").HasColumnType("uuid").HasColumnName("created_by");
            b.Property<int>("Decimals").HasColumnType("integer").HasColumnName("decimals");
            b.Property<string>("IsoCode").IsRequired().HasMaxLength(3).HasColumnType("character varying(3)").HasColumnName("iso_code");
            b.Property<string>("Name").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)").HasColumnName("name");
            b.Property<long>("RowVersion").IsConcurrencyToken().HasColumnType("bigint").HasColumnName("row_version");
            b.Property<string>("Symbol").IsRequired().HasMaxLength(10).HasColumnType("character varying(10)").HasColumnName("symbol");
            b.Property<Guid>("TenantId").HasColumnType("uuid").HasColumnName("tenant_id");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamptz").HasColumnName("updated_at");
            b.Property<Guid>("UpdatedBy").HasColumnType("uuid").HasColumnName("updated_by");

            b.HasKey("Id");
            b.HasIndex("TenantId");
            b.HasIndex("TenantId", "IsoCode").IsUnique();
            b.ToTable("currency", "core");
        });

        modelBuilder.Entity("Erp.Core.Domain.Entities.Tenant", b =>
        {
            b.Property<Guid>("Id").HasColumnType("uuid").HasColumnName("id");
            b.Property<Guid?>("CompanyId").HasColumnType("uuid").HasColumnName("company_id");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamptz").HasColumnName("created_at");
            b.Property<Guid>("CreatedBy").HasColumnType("uuid").HasColumnName("created_by");
            b.Property<string>("DefaultCountryCode").IsRequired().HasMaxLength(2).HasColumnType("character varying(2)").HasColumnName("default_country_code");
            b.Property<bool>("IsActive").HasColumnType("boolean").HasColumnName("is_active");
            b.Property<string>("Name").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)").HasColumnName("name");
            b.Property<long>("RowVersion").IsConcurrencyToken().HasColumnType("bigint").HasColumnName("row_version");
            b.Property<string>("Slug").IsRequired().HasMaxLength(100).HasColumnType("character varying(100)").HasColumnName("slug");
            b.Property<Guid>("TenantId").HasColumnType("uuid").HasColumnName("tenant_id");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamptz").HasColumnName("updated_at");
            b.Property<Guid>("UpdatedBy").HasColumnType("uuid").HasColumnName("updated_by");

            b.HasKey("Id");
            b.HasIndex("Slug").IsUnique();
            b.HasIndex("TenantId");
            b.ToTable("tenant", "core");
        });

        modelBuilder.Entity("Erp.Core.Domain.Entities.User", b =>
        {
            b.Property<Guid>("Id").HasColumnType("uuid").HasColumnName("id");
            b.Property<List<Guid>>("AccessibleCompanyIds").IsRequired().HasColumnType("uuid[]").HasColumnName("accessible_company_ids");
            b.Property<Guid?>("CompanyId").HasColumnType("uuid").HasColumnName("company_id");
            b.Property<DateTimeOffset>("CreatedAt").HasColumnType("timestamptz").HasColumnName("created_at");
            b.Property<Guid>("CreatedBy").HasColumnType("uuid").HasColumnName("created_by");
            b.Property<string>("DisplayName").IsRequired().HasMaxLength(200).HasColumnType("character varying(200)").HasColumnName("display_name");
            b.Property<string>("Email").IsRequired().HasMaxLength(320).HasColumnType("character varying(320)").HasColumnName("email");
            b.Property<string>("ExternalSubject").IsRequired().HasMaxLength(255).HasColumnType("character varying(255)").HasColumnName("external_subject");
            b.Property<bool>("IsActive").HasColumnType("boolean").HasColumnName("is_active");
            b.Property<List<string>>("Roles").IsRequired().HasColumnType("text[]").HasColumnName("roles");
            b.Property<long>("RowVersion").IsConcurrencyToken().HasColumnType("bigint").HasColumnName("row_version");
            b.Property<Guid>("TenantId").HasColumnType("uuid").HasColumnName("tenant_id");
            b.Property<DateTimeOffset>("UpdatedAt").HasColumnType("timestamptz").HasColumnName("updated_at");
            b.Property<Guid>("UpdatedBy").HasColumnType("uuid").HasColumnName("updated_by");

            b.HasKey("Id");
            b.HasIndex("TenantId");
            b.HasIndex("TenantId", "Email").IsUnique();
            b.HasIndex("TenantId", "ExternalSubject").IsUnique();
            b.ToTable("user", "core");
        });
#pragma warning restore 612, 618
    }
}

