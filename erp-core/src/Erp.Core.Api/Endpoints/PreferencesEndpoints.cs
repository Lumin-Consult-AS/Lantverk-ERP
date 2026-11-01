using System.Text.RegularExpressions;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Erp.Core.Api.Endpoints;

/// <param name="Language">A supported language code, or null/empty to use the tenant's default.</param>
/// <param name="Region">A country code, or null/empty to use the tenant's default.</param>
public sealed record SetPreferencesRequest(string? Language, string? Region);

public sealed record SetTenantPreferencesRequest(string? DefaultLanguage, string? DefaultRegion);

/// <summary>
/// Language and region of a user, with the tenant's defaults for those who have not chosen (ADR-0033). The core keeps only the
/// codes; formatting dates, numbers and amounts for a region is done by whoever shows them. The data API is not affected: dates
/// are always ISO 8601 and numbers always have a decimal point.
/// </summary>
public static class PreferencesEndpoints
{
    /// <summary>The languages the platform has texts for.</summary>
    public static readonly IReadOnlyList<string> Languages = ["nb", "en"];

    private static readonly Regex RegionPattern = new("^[A-Z]{2}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static void MapPreferencesEndpoints(this IEndpointRouteBuilder v1)
    {
        v1.MapGet("/me/preferences", async (CoreDbContext db, ITenantContext tenantContext, CancellationToken ct) =>
        {
            var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(ct);
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == tenantContext.UserId, ct);
            return tenant is null || user is null ? Results.NotFound() : Results.Ok(Describe(user.Language, user.Region, tenant.DefaultLanguage, tenant.DefaultCountryCode));
        });

        v1.MapPut("/me/preferences", async (SetPreferencesRequest request, CoreDbContext db, ITenantContext tenantContext, CancellationToken ct) =>
        {
            if (Validate(request.Language, request.Region, out var language, out var region) is { } error)
            {
                return Results.BadRequest(new { error });
            }

            var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(ct);
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == tenantContext.UserId, ct);
            if (tenant is null || user is null)
            {
                return Results.NotFound();
            }

            user.Language = language;
            user.Region = region;
            await db.SaveChangesAsync(ct);
            return Results.Ok(Describe(user.Language, user.Region, tenant.DefaultLanguage, tenant.DefaultCountryCode));
        });

        v1.MapPut("/tenant/preferences", async (SetTenantPreferencesRequest request, CoreDbContext db, CancellationToken ct) =>
        {
            if (Validate(request.DefaultLanguage, request.DefaultRegion, out var language, out var region) is { } error)
            {
                return Results.BadRequest(new { error });
            }

            if (language is null || region is null)
            {
                return Results.BadRequest(new { error = "Both a default language and a default region are required." });
            }

            var tenant = await db.Tenants.FirstOrDefaultAsync(ct);
            if (tenant is null)
            {
                return Results.NotFound();
            }

            tenant.DefaultLanguage = language;
            tenant.DefaultCountryCode = region;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { defaultLanguage = tenant.DefaultLanguage, defaultRegion = tenant.DefaultCountryCode });
        }).AddEndpointFilter(EventEndpoints.RequireTenantAdminAsync);
    }

    /// <summary>The user's choice if there is one, otherwise the tenant's default. Used by the session and the preference endpoints.</summary>
    public static object Describe(string? userLanguage, string? userRegion, string tenantLanguage, string tenantRegion) => new
    {
        language = userLanguage,
        region = userRegion,
        effectiveLanguage = userLanguage ?? tenantLanguage,
        effectiveRegion = userRegion ?? tenantRegion,
        tenantDefaultLanguage = tenantLanguage,
        tenantDefaultRegion = tenantRegion,
        languages = Languages
    };

    private static string? Validate(string? languageInput, string? regionInput, out string? language, out string? region)
    {
        language = string.IsNullOrWhiteSpace(languageInput) ? null : languageInput.Trim().ToLowerInvariant();
        region = string.IsNullOrWhiteSpace(regionInput) ? null : regionInput.Trim().ToUpperInvariant();
        if (language is not null && !Languages.Contains(language))
        {
            return $"Language '{languageInput}' is not supported. Use one of: {string.Join(", ", Languages)}.";
        }

        if (region is not null && !RegionPattern.IsMatch(region))
        {
            return $"Region '{regionInput}' is not a country code (two letters, e.g. NO).";
        }

        return null;
    }
}
