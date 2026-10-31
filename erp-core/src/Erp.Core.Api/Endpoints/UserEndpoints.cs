using Erp.Core.Domain.Entities;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Erp.Core.Api.Endpoints;

public sealed record CreateUserRequest(
    string? ExternalSubject,
    string? Email,
    string? DisplayName,
    IReadOnlyList<string>? Roles = null,
    IReadOnlyList<Guid>? CompanyIds = null);

public sealed record SetUserRolesRequest(IReadOnlyList<string>? Roles);

/// <summary>
/// User administration for tenant administrators: add a user (mapped to the person's identity-provider subject), give roles,
/// and deactivate. The identity provider itself (accounts, passwords) is not managed here. Roles take effect when the user's
/// next token is issued, which is at most the token lifetime away.
/// </summary>
public static class UserEndpoints
{
    private const string AdminRole = "tenant.admin";
    private const string BaseRole = "core.user";

    public static void MapUserEndpoints(this IEndpointRouteBuilder v1)
    {
        var users = v1.MapGroup("/users").AddEndpointFilter(EventEndpoints.RequireTenantAdminAsync);

        users.MapGet("/", async (CoreDbContext db, CancellationToken ct) =>
            Results.Ok((await db.Users.AsNoTracking().OrderBy(u => u.DisplayName).ToListAsync(ct)).Select(ToDto)));

        users.MapPost("/", async (CreateUserRequest request, CoreDbContext db, ITenantContext tenant, CancellationToken ct) =>
        {
            var subject = request.ExternalSubject?.Trim();
            var email = request.Email?.Trim();
            var name = request.DisplayName?.Trim();
            if (string.IsNullOrEmpty(subject) || string.IsNullOrEmpty(email) || string.IsNullOrEmpty(name))
            {
                return Results.BadRequest(new { error = "externalSubject, email and displayName are required." });
            }

            if (await db.Users.AnyAsync(u => u.ExternalSubject == subject, ct))
            {
                return Results.Conflict(new { error = $"A user with subject '{subject}' already exists." });
            }

            var companies = request.CompanyIds is { Count: > 0 }
                ? request.CompanyIds.Distinct().ToList()
                : tenant.CompanyId is { } own ? [own] : new List<Guid>();
            var user = new User
            {
                CompanyId = tenant.CompanyId,
                ExternalSubject = subject,
                Email = email,
                DisplayName = name,
                Roles = NormalizeRoles(request.Roles),
                AccessibleCompanyIds = companies,
                IsActive = true
            };
            db.Users.Add(user);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/users/{user.Id}", ToDto(user));
        });

        users.MapPut("/{id:guid}/roles", async (Guid id, SetUserRolesRequest request, CoreDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
            if (user is null)
            {
                return Results.NotFound(new { error = $"User '{id}' was not found." });
            }

            var roles = NormalizeRoles(request.Roles);
            if (IsAdmin(user) && !roles.Contains(AdminRole, StringComparer.OrdinalIgnoreCase) && await IsLastAdminAsync(db, user, ct))
            {
                return Results.Conflict(new { error = "The last active administrator cannot lose the administrator role." });
            }

            user.Roles = roles;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(user));
        });

        users.MapPost("/{id:guid}/deactivate", async (Guid id, CoreDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
            if (user is null)
            {
                return Results.NotFound(new { error = $"User '{id}' was not found." });
            }

            if (IsAdmin(user) && await IsLastAdminAsync(db, user, ct))
            {
                return Results.Conflict(new { error = "The last active administrator cannot be deactivated." });
            }

            user.IsActive = false;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(user));
        });

        users.MapPost("/{id:guid}/activate", async (Guid id, CoreDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
            if (user is null)
            {
                return Results.NotFound(new { error = $"User '{id}' was not found." });
            }

            user.IsActive = true;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(user));
        });
    }

    private static List<string> NormalizeRoles(IReadOnlyList<string>? roles)
    {
        var result = (roles ?? [])
            .Select(r => r?.Trim() ?? string.Empty)
            .Where(r => r.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!result.Contains(BaseRole, StringComparer.OrdinalIgnoreCase))
        {
            result.Add(BaseRole);
        }

        return result;
    }

    private static bool IsAdmin(User user) => user.IsActive && user.Roles.Contains(AdminRole, StringComparer.OrdinalIgnoreCase);

    private static async Task<bool> IsLastAdminAsync(CoreDbContext db, User user, CancellationToken ct) =>
        !await db.Users.AnyAsync(u => u.Id != user.Id && u.IsActive && u.Roles.Contains(AdminRole), ct);

    private static object ToDto(User u) => new
    {
        u.Id,
        u.ExternalSubject,
        u.Email,
        u.DisplayName,
        u.Roles,
        companyIds = u.AccessibleCompanyIds,
        u.IsActive
    };
}
