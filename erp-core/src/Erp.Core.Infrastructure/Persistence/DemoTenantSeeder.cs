using Erp.Core.Domain.Entities;
using Erp.Core.Domain.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Erp.Core.Infrastructure.Persistence;

/// <summary>
/// Seeds the local demo tenants, countries, currencies, companies, and users
/// through the RLS-protected <see cref="CoreDbContext"/>.
/// </summary>
public static class DemoTenantSeeder
{
    public static readonly Guid DemoTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid DemoCompanyId = Guid.Parse("11111111-1111-1111-1111-111111111101");
    public static readonly Guid DemoUserId = Guid.Parse("11111111-1111-1111-1111-111111111201");
    public const string DemoUserSubject = "demo-user-1";

    public static readonly Guid SecondTenantId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid SecondCompanyId = Guid.Parse("22222222-2222-2222-2222-222222222101");
    public static readonly Guid SecondUserId = Guid.Parse("22222222-2222-2222-2222-222222222201");
    public const string SecondUserSubject = "demo-user-2";

    public static async Task SeedAsync(
        CoreDbContext dbContext,
        ITenantContext tenantContext,
        CancellationToken cancellationToken = default)
    {
        await SeedTenantAsync(
            dbContext,
            tenantContext,
            tenantId: DemoTenantId,
            tenantName: "Demo Handel",
            tenantSlug: "demo-handel",
            companyId: DemoCompanyId,
            companyName: "Demo Handel Norge AS",
            orgNumber: "999888777",
            userId: DemoUserId,
            externalSubject: DemoUserSubject,
            userEmail: "demo@demohandel.no",
            userDisplayName: "Jens Demo",
            cancellationToken: cancellationToken);

        await SeedTenantAsync(
            dbContext,
            tenantContext,
            tenantId: SecondTenantId,
            tenantName: "Nordisk Import",
            tenantSlug: "nordisk-import",
            companyId: SecondCompanyId,
            companyName: "Nordisk Import AS",
            orgNumber: "888777666",
            userId: SecondUserId,
            externalSubject: SecondUserSubject,
            userEmail: "anna@nordiskimport.no",
            userDisplayName: "Anna Nordisk",
            cancellationToken: cancellationToken);
    }

    private static async Task SeedTenantAsync(
        CoreDbContext dbContext,
        ITenantContext tenantContext,
        Guid tenantId,
        string tenantName,
        string tenantSlug,
        Guid companyId,
        string companyName,
        string orgNumber,
        Guid userId,
        string externalSubject,
        string userEmail,
        string userDisplayName,
        CancellationToken cancellationToken)
    {
        using var scope = tenantContext.BeginScope(tenantId, userId, companyId, ["tenant.admin"]);
        dbContext.ChangeTracker.Clear();

        if (await dbContext.Tenants.AnyAsync(t => t.Id == tenantId, cancellationToken))
        {
            return;
        }

        var tenant = new Tenant
        {
            Id = tenantId,
            TenantId = tenantId,
            Name = tenantName,
            Slug = tenantSlug,
            DefaultCountryCode = "NO",
            IsActive = true
        };

        var norway = new Country
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            IsoCode = "NO",
            Name = "Norge",
            DefaultCurrencyCode = "NOK"
        };

        var sweden = new Country
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            IsoCode = "SE",
            Name = "Sverige",
            DefaultCurrencyCode = "SEK"
        };

        var denmark = new Country
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            IsoCode = "DK",
            Name = "Danmark",
            DefaultCurrencyCode = "DKK"
        };

        var nok = new Currency
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            IsoCode = "NOK",
            Name = "Norske kroner",
            Symbol = "kr",
            Decimals = 2
        };

        var sek = new Currency
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            IsoCode = "SEK",
            Name = "Svenske kroner",
            Symbol = "kr",
            Decimals = 2
        };

        var dkk = new Currency
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            IsoCode = "DKK",
            Name = "Danske kroner",
            Symbol = "kr",
            Decimals = 2
        };

        var eur = new Currency
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            IsoCode = "EUR",
            Name = "Euro",
            Symbol = "€",
            Decimals = 2
        };

        var company = new Company
        {
            Id = companyId,
            TenantId = tenantId,
            CompanyId = companyId,
            Name = companyName,
            OrganizationNumber = orgNumber,
            CountryId = norway.Id,
            BaseCurrencyId = nok.Id,
            IsActive = true
        };

        var user = new User
        {
            Id = userId,
            TenantId = tenantId,
            CompanyId = companyId,
            ExternalSubject = externalSubject,
            Email = userEmail,
            DisplayName = userDisplayName,
            Roles = ["tenant.admin", "core.user"],
            AccessibleCompanyIds = [companyId],
            IsActive = true
        };

        dbContext.Tenants.Add(tenant);
        dbContext.Countries.AddRange(norway, sweden, denmark);
        dbContext.Currencies.AddRange(nok, sek, dkk, eur);
        dbContext.Companies.Add(company);
        dbContext.Users.Add(user);

        await dbContext.SaveChangesAsync(cancellationToken);
        dbContext.ChangeTracker.Clear();
    }
}

