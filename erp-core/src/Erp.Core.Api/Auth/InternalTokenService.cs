using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Erp.Core.Api.Auth;

public sealed record TokenExchangeRequest(Guid? CompanyId = null, string? TenantSlug = null);

public sealed record TokenExchangeResponse(
    string AccessToken,
    string TokenType,
    int ExpiresIn,
    Guid TenantId,
    string TenantName,
    Guid? CompanyId,
    string? CompanyName,
    Guid UserId,
    string DisplayName,
    IReadOnlyList<string> Roles);

/// <summary>
/// Exchanges a validated external OIDC JWT for a short-lived internal platform token
/// scoped to a single tenant, user, role set, and active company.
/// </summary>
public sealed class InternalTokenService(
    CoreDbContext dbContext,
    ITenantContext tenantContext,
    IOptions<AuthOptions> authOptions) : IInternalTokenIssuer
{
    private readonly AuthOptions _options = authOptions.Value;

    public string IssueShortLivedCallToken(
        Guid tenantId,
        Guid userId,
        Guid? companyId = null,
        IReadOnlyList<string>? roles = null,
        TimeSpan? lifetime = null)
    {
        var now = DateTime.UtcNow;
        var expires = now.Add(lifetime ?? TimeSpan.FromMinutes(2));

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString("D")),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("D")),
            new("tenant_id", tenantId.ToString("D")),
            new("token_use", "internal")
        };

        if (companyId.HasValue)
        {
            claims.Add(new Claim("company_id", companyId.Value.ToString("D")));
        }

        if (roles is not null)
        {
            foreach (var role in roles)
            {
                claims.Add(new Claim(ClaimTypes.Role, role));
                claims.Add(new Claim("role", role));
            }
        }

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.InternalSigningKey));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var jwt = new JwtSecurityToken(
            issuer: _options.InternalIssuer,
            audience: _options.InternalAudience,
            claims: claims,
            notBefore: now,
            expires: expires,
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    public ValidatedInternalToken? ValidateToken(string accessToken)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        try
        {
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var keyBytes = Encoding.UTF8.GetBytes(_options.InternalSigningKey);
            var principal = handler.ValidateToken(
                accessToken,
                new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = _options.InternalIssuer,
                    ValidateAudience = true,
                    ValidAudience = _options.InternalAudience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(15),
                    NameClaimType = "sub",
                    RoleClaimType = "role"
                },
                out _);

            if (!string.Equals(principal.FindFirstValue("token_use"), "internal", StringComparison.Ordinal))
            {
                return null;
            }

            if (!Guid.TryParse(principal.FindFirstValue("tenant_id"), out var tenantId)
                || !Guid.TryParse(principal.FindFirstValue("sub"), out var userId))
            {
                return null;
            }

            Guid? companyId = Guid.TryParse(principal.FindFirstValue("company_id"), out var cid) ? cid : null;
            var roles = principal.FindAll("role").Select(c => c.Value).Distinct().ToList();

            return new ValidatedInternalToken(tenantId, userId, companyId, roles, principal);
        }
        catch
        {
            return null;
        }
    }

    public async Task<TokenExchangeResponse?> ExchangeTokenAsync(
        ClaimsPrincipal externalPrincipal,
        TokenExchangeRequest? request,
        CancellationToken cancellationToken = default)
    {
        var externalSub = externalPrincipal.FindFirstValue("sub")
            ?? externalPrincipal.FindFirstValue(ClaimTypes.NameIdentifier);

        if (string.IsNullOrWhiteSpace(externalSub))
        {
            return null;
        }

        Guid? tenantId = null;
        var tenantClaim = externalPrincipal.FindFirstValue("tenant_id");
        if (Guid.TryParse(tenantClaim, out var parsedTenantClaim))
        {
            tenantId = parsedTenantClaim;
        }

        var tenantSlug = request?.TenantSlug ?? externalPrincipal.FindFirstValue("tenant_slug");
        if (!tenantId.HasValue || !string.IsNullOrWhiteSpace(tenantSlug))
        {
            tenantId = await dbContext.ResolveLoginTenantAsync(externalSub, tenantSlug, cancellationToken);
        }

        if (!tenantId.HasValue)
        {
            return null;
        }

        using var scope = tenantContext.BeginScope(tenantId.Value);

        var tenant = await dbContext.Tenants
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == tenantId.Value && t.IsActive, cancellationToken);

        if (tenant is null)
        {
            return null;
        }

        var user = await dbContext.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.ExternalSubject == externalSub && u.IsActive, cancellationToken);

        if (user is null)
        {
            return null;
        }

        Guid? activeCompanyId = request?.CompanyId ?? user.CompanyId;
        if (activeCompanyId.HasValue
            && user.AccessibleCompanyIds.Count > 0
            && !user.AccessibleCompanyIds.Contains(activeCompanyId.Value))
        {
            return null;
        }

        if (!activeCompanyId.HasValue && user.AccessibleCompanyIds.Count > 0)
        {
            activeCompanyId = user.AccessibleCompanyIds[0];
        }

        string? companyName = null;
        if (activeCompanyId.HasValue)
        {
            var company = await dbContext.Companies
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == activeCompanyId.Value && c.IsActive, cancellationToken);

            if (company is null)
            {
                return null;
            }

            companyName = company.Name;
        }

        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(_options.InternalTokenLifetimeMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString("D")),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("D")),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("name", user.DisplayName),
            new("external_sub", user.ExternalSubject),
            new("tenant_id", tenant.Id.ToString("D")),
            new("tenant_slug", tenant.Slug),
            new("token_use", "internal")
        };

        if (activeCompanyId.HasValue)
        {
            claims.Add(new Claim("company_id", activeCompanyId.Value.ToString("D")));
        }

        foreach (var role in user.Roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
            claims.Add(new Claim("role", role));
        }

        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.InternalSigningKey));
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.HmacSha256);

        var jwt = new JwtSecurityToken(
            issuer: _options.InternalIssuer,
            audience: _options.InternalAudience,
            claims: claims,
            notBefore: now,
            expires: expires,
            signingCredentials: credentials);

        var tokenString = new JwtSecurityTokenHandler().WriteToken(jwt);
        var expiresInSeconds = (int)TimeSpan.FromMinutes(_options.InternalTokenLifetimeMinutes).TotalSeconds;

        return new TokenExchangeResponse(
            AccessToken: tokenString,
            TokenType: "Bearer",
            ExpiresIn: expiresInSeconds,
            TenantId: tenant.Id,
            TenantName: tenant.Name,
            CompanyId: activeCompanyId,
            CompanyName: companyName,
            UserId: user.Id,
            DisplayName: user.DisplayName,
            Roles: user.Roles);
    }
}
