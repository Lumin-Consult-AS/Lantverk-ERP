using System.Security.Claims;
using System.Text;
using System.Text.Json.Serialization;
using Erp.Core.Api;
using Erp.Core.Api.Auth;
using Erp.Core.Api.Endpoints;
using Erp.Core.Api.Grpc;
using Erp.Core.Api.Middleware;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Apps;
using Erp.Core.Infrastructure.Attention;
using Erp.Core.Infrastructure.Events;
using Erp.Core.Infrastructure.Notifications;
using Erp.Core.Infrastructure.Security;
using Erp.Core.Infrastructure.Workflow;
using Microsoft.Extensions.Options;
using Erp.Core.Infrastructure.Data;
using Erp.Core.Infrastructure.Metadata;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));

builder.Services.AddScoped<ITenantContext, TenantContext>();
builder.Services.AddScoped<TenantSessionInterceptor>();
builder.Services.AddScoped<InternalTokenService>();
builder.Services.AddScoped<IInternalTokenIssuer>(sp => sp.GetRequiredService<InternalTokenService>());
builder.Services.AddSingleton<KubernetesAppOrchestrator>();
builder.Services.AddSingleton<ActiveCallTransactionRegistry>();
builder.Services.AddSingleton<AppHostOptions>();
builder.Services.AddScoped<AppHostService>();
builder.Services.AddSingleton<EventBusOptions>();
builder.Services.AddSingleton<EventDeliveryWakeSignal>();
builder.Services.AddScoped<EventPublisher>();
builder.Services.AddScoped<EventLogService>();
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<EmailOptions>>().Value);
builder.Services.AddSingleton<IEmailSender>(sp =>
    sp.GetRequiredService<EmailOptions>().SmtpHost is { Length: > 0 }
        ? new SmtpEmailSender(sp.GetRequiredService<EmailOptions>())
        : ActivatorUtilities.CreateInstance<LoggingEmailSender>(sp));
builder.Services.AddSingleton<TaskLinkTokens>();
builder.Services.AddSingleton<AttentionCache>();
builder.Services.AddScoped<PermissionService>();
builder.Services.AddScoped<WorkflowStore>();
builder.Services.AddScoped<WorkflowNotifier>();
builder.Services.AddScoped<WorkflowEngine>();
builder.Services.AddScoped<TaskLinkResponder>();
builder.Services.AddScoped<AttentionService>();
builder.Services.AddHostedService<TaskDeadlineWorker>();
builder.Services.AddHttpClient(EventDeliveryWorker.WebhookHttpClientName);
builder.Services.AddHostedService<EventDeliveryWorker>();
builder.Services.AddScoped<MetadataRegistryService>();
builder.Services.AddScoped<RecordDataService>();
builder.Services.AddSingleton<IAppPackageStorage>(_ => new LocalAppPackageStorage());
builder.Services.AddScoped<AppInstallationService>();
builder.Services.AddGrpc(options => options.Interceptors.Add<PermissionExceptionInterceptor>());

builder.Services.AddDbContext<CoreDbContext>((sp, options) =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var appConnStr = config.GetConnectionString("ApplicationConnection");
    if (string.IsNullOrWhiteSpace(appConnStr))
    {
        var baseConnStr = config.GetConnectionString("DefaultConnection")
            ?? "Host=localhost;Port=5432;Database=erp;Username=erp_migrator;Password=erp_migrator_dev";
        appConnStr = DatabaseInitializer.BuildApplicationConnectionString(baseConnStr);
    }

    options.UseNpgsql(
        appConnStr,
        npgsql => npgsql.MigrationsHistoryTable("__ef_migrations_history", CoreDbContext.SchemaName));
    options.AddInterceptors(sp.GetRequiredService<TenantSessionInterceptor>());
});

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = AuthSchemes.InternalJwt;
        options.DefaultChallengeScheme = AuthSchemes.InternalJwt;
    })
    .AddJwtBearer(AuthSchemes.InternalJwt)
    .AddJwtBearer(AuthSchemes.ExternalOidc);

builder.Services.AddOptions<JwtBearerOptions>(AuthSchemes.InternalJwt)
    .Configure<Microsoft.Extensions.Options.IOptions<AuthOptions>>((options, authOptsAccessor) =>
    {
        var authOptions = authOptsAccessor.Value;
        options.MapInboundClaims = false;
        var keyBytes = Encoding.UTF8.GetBytes(authOptions.InternalSigningKey);
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = authOptions.InternalIssuer,
            ValidateAudience = true,
            ValidAudience = authOptions.InternalAudience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(15),
            NameClaimType = "sub",
            RoleClaimType = "role"
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = ctx =>
            {
                var tokenUse = ctx.Principal?.FindFirstValue("token_use");
                if (!string.Equals(tokenUse, "internal", StringComparison.Ordinal))
                {
                    ctx.Fail("Only internal platform tokens are accepted on this scheme.");
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddOptions<JwtBearerOptions>(AuthSchemes.ExternalOidc)
    .Configure<Microsoft.Extensions.Options.IOptions<AuthOptions>>((options, authOptsAccessor) =>
    {
        var authOptions = authOptsAccessor.Value;
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = authOptions.RequireHttpsMetadata;

        if (!string.IsNullOrWhiteSpace(authOptions.OidcTestSigningKey))
        {
            var testKeyBytes = Encoding.UTF8.GetBytes(authOptions.OidcTestSigningKey);
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = authOptions.OidcTestIssuer,
                ValidateAudience = true,
                ValidAudience = authOptions.OidcAudience,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(testKeyBytes),
                ValidateLifetime = true,
                ClockSkew = TimeSpan.FromSeconds(15),
                NameClaimType = "sub",
                RoleClaimType = "role"
            };
        }
        else
        {
            options.Authority = authOptions.OidcAuthority;
            options.Audience = authOptions.OidcAudience;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidAudience = authOptions.OidcAudience,
                ValidateLifetime = true,
                NameClaimType = "sub"
            };
        }
    });

builder.Services.AddAuthorizationBuilder()
    .SetDefaultPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(AuthSchemes.InternalJwt)
        .RequireAuthenticatedUser()
        .RequireClaim("token_use", "internal")
        .Build())
    .AddPolicy("ExternalOidcPolicy", policy =>
    {
        policy.AddAuthenticationSchemes(AuthSchemes.ExternalOidc);
        policy.RequireAuthenticatedUser();
    });

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod()
              .WithExposedHeaders("ETag"));
});

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Database:AutoMigrateAndSeed"))
{
    var migrationConnStr = app.Configuration.GetConnectionString("MigrationConnection")
        ?? app.Configuration.GetConnectionString("DefaultConnection")
        ?? "Host=localhost;Port=5432;Database=erp;Username=erp_migrator;Password=erp_migrator_dev";

    await DatabaseInitializer.MigrateAsync(migrationConnStr);

    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<CoreDbContext>();
    var tenantContext = scope.ServiceProvider.GetRequiredService<ITenantContext>();
    await DemoTenantSeeder.SeedAsync(db, tenantContext);
}

app.UseCors();
app.UseAuthentication();
app.UseMiddleware<TenantContextMiddleware>();
app.UseAuthorization();

app.MapGet("/api/v1/health", () => Results.Ok(new { status = "healthy" }))
    .AllowAnonymous();

app.MapPost("/api/v1/auth/token", async (
        HttpContext httpContext,
        InternalTokenService tokenService,
        TokenExchangeRequest? request,
        CancellationToken cancellationToken) =>
    {
        var exchanged = await tokenService.ExchangeTokenAsync(httpContext.User, request, cancellationToken);
        return exchanged is not null
            ? Results.Ok(exchanged)
            : Results.Forbid();
    })
    .RequireAuthorization("ExternalOidcPolicy");

app.MapPost("/api/v1/auth/dev-login", async (
        IHostEnvironment env,
        IConfiguration config,
        InternalTokenService tokenService,
        DevLoginRequest? request,
        CancellationToken cancellationToken) =>
    {
        if (!env.IsDevelopment() && !config.GetValue<bool>("Auth:EnableDevLogin"))
        {
            return Results.NotFound();
        }

        var subject = string.IsNullOrWhiteSpace(request?.Subject)
            ? DemoTenantSeeder.DemoUserSubject
            : request.Subject;

        var identity = new ClaimsIdentity([new Claim("sub", subject)], "DevLogin");
        var principal = new ClaimsPrincipal(identity);
        var exchanged = await tokenService.ExchangeTokenAsync(
            principal,
            new TokenExchangeRequest(request?.CompanyId, request?.TenantSlug),
            cancellationToken);

        return exchanged is not null ? Results.Ok(exchanged) : Results.Forbid();
    })
    .AllowAnonymous();

var v1 = app.MapGroup("/api/v1").RequireAuthorization();

v1.MapGet("/session", async (
    CoreDbContext db,
    ITenantContext tenantContext,
    CancellationToken cancellationToken) =>
{
    var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(cancellationToken);
    if (tenant is null)
    {
        return Results.NotFound();
    }

    var user = await db.Users.AsNoTracking()
        .FirstOrDefaultAsync(u => u.Id == tenantContext.UserId, cancellationToken);

    var company = tenantContext.CompanyId.HasValue
        ? await db.Companies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == tenantContext.CompanyId.Value, cancellationToken)
        : null;

    var installedApps = await db.InstalledApps.AsNoTracking()
        .OrderBy(a => a.AppId)
        .Select(a => new { a.AppId, a.Version, a.Name, a.IsDeclarative })
        .ToListAsync(cancellationToken);

    return Results.Ok(new
    {
        tenant = new
        {
            tenant.Id,
            tenant.Name,
            tenant.Slug,
            tenant.DefaultCountryCode
        },
        company = company is null ? null : new
        {
            company.Id,
            company.Name,
            company.OrganizationNumber,
            company.CountryId,
            company.BaseCurrencyId
        },
        user = user is null ? null : new
        {
            user.Id,
            user.Email,
            user.DisplayName,
            user.Roles,
            user.AccessibleCompanyIds
        },
        installedApps,
        navigation = Array.Empty<object>()
    });
});

// Deliberately no .Where(x => x.TenantId == ...) filters in C# code:
// PostgreSQL Row-Level Security (RLS) enforces tenant isolation on all tables.
v1.MapGet("/core/tenants", async (CoreDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Tenants.AsNoTracking().OrderBy(t => t.Name).ToListAsync(ct)));

v1.MapGet("/core/companies", async (CoreDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Companies.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct)));

v1.MapGet("/core/countries", async (CoreDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Countries.AsNoTracking().OrderBy(c => c.IsoCode).ToListAsync(ct)));

v1.MapGet("/core/currencies", async (CoreDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Currencies.AsNoTracking().OrderBy(c => c.IsoCode).ToListAsync(ct)));

v1.MapGet("/core/users", async (CoreDbContext db, CancellationToken ct) =>
    Results.Ok(await db.Users.AsNoTracking().OrderBy(u => u.DisplayName).ToListAsync(ct)));

v1.MapMetadataAndDataEndpoints();
v1.MapAppManagementEndpoints();
v1.MapEventEndpoints();
v1.MapWorkflowEndpoints();
v1.MapRoleEndpoints();
app.MapGroup("/api/v1").MapTaskResponseEndpoints();

app.MapGrpcService<CoreDataGrpcService>();
app.MapGrpcService<CoreEventGrpcService>();
app.MapGrpcService<CoreWorkflowGrpcService>();
app.MapGrpcService<CoreDocumentGrpcService>();

app.MapGet("/", () => Results.Content(LandingPageHtml.Content, "text/html; charset=utf-8"))
    .AllowAnonymous();

app.Run();

public sealed record DevLoginRequest(string? Subject = null, string? TenantSlug = null, Guid? CompanyId = null);

public partial class Program;
