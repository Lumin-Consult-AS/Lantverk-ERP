namespace Erp.Core.Domain.Entities;

/// <summary>
/// Represents an application installed in a tenant (<c>core.installed_app</c>).
/// </summary>
public sealed class InstalledApp : EntityBase
{
    public string AppId { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string Name { get; set; } = string.Empty;
    public string? Publisher { get; set; }
    public bool IsDeclarative { get; set; } = true;
    public string? ContainerImage { get; set; }
    public string PackageSha256 { get; set; } = string.Empty;
    public string PackageStorageKey { get; set; } = string.Empty;
    public string ManifestJson { get; set; } = "{}";
    public string PagesJson { get; set; } = "[]";
    public string PermissionSetsJson { get; set; } = "[]";
    public string TranslationsJson { get; set; } = "{}";
}

