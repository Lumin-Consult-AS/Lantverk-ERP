namespace Erp.Core.Api.Auth;

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public string OidcAuthority { get; set; } = "http://localhost:8080/realms/erp-dev";
    public string OidcAudience { get; set; } = "erp-web";
    public bool RequireHttpsMetadata { get; set; } = false;

    /// <summary>
    /// Optional symmetric key used in automated tests to validate simulated external OIDC tokens
    /// when a live OIDC metadata endpoint is not used.
    /// </summary>
    public string? OidcTestSigningKey { get; set; }
    public string OidcTestIssuer { get; set; } = "https://oidc.test.local/realms/erp-dev";

    public string InternalIssuer { get; set; } = "erp-core";
    public string InternalAudience { get; set; } = "erp-internal";
    public string InternalSigningKey { get; set; } = "dev-only-internal-signing-key-must-be-at-least-32-bytes!";
    public int InternalTokenLifetimeMinutes { get; set; } = 15;
}

