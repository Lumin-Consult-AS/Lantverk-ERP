using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Erp.Core.Domain.Apps;

namespace Erp.Core.Infrastructure.Apps;

public sealed record AppPackageSignatureDocument(
    string Algorithm,
    string Publisher,
    string ContentSha256,
    string Signature,
    DateTimeOffset SignedAt);

public sealed record VerifiedAppPackage(
    AppManifest Manifest,
    string RawManifestYaml,
    string PackageSha256,
    IReadOnlyDictionary<string, string> Files,
    byte[] ZipBytes);

/// <summary>
/// Packs, signs, and verifies <c>.erpapp.zip</c> application packages containing
/// <c>app.yaml</c>, <c>pages/</c>, <c>permissions/</c>, <c>i18n/</c>, and <c>SIGNATURE</c>.
/// </summary>
public static class AppPackageFormat
{
    public const string SignatureEntryName = "SIGNATURE";
    public const string ManifestEntryName = "app.yaml";
    public const string DefaultDevSigningKey = "erp-dev-package-signing-key-change-in-production-2026";

    public static byte[] CreateSignedZipFromDirectory(
        string directoryPath,
        string signingKey = DefaultDevSigningKey,
        string? publisher = null)
    {
        if (!Directory.Exists(directoryPath))
        {
            throw new DirectoryNotFoundException($"App package source directory '{directoryPath}' was not found.");
        }

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            var relPath = Path.GetRelativePath(directoryPath, filePath).Replace('\\', '/');
            if (string.Equals(relPath, SignatureEntryName, StringComparison.OrdinalIgnoreCase)
                || !IsPackageContent(relPath))
            {
                continue;
            }

            files[relPath] = File.ReadAllText(filePath, Encoding.UTF8);
        }

        return CreateSignedZipFromFiles(files, signingKey, publisher);
    }

    /// <summary>
    /// Packages contain only declarative YAML. Container apps keep their source and build output
    /// (<c>bin/</c>, <c>obj/</c>, <c>*.cs</c>, Dockerfile) next to the manifest; those are not package content.
    /// </summary>
    private static bool IsPackageContent(string relPath)
    {
        var firstSegment = relPath.Split('/', 2)[0];
        if (firstSegment is "bin" or "obj")
        {
            return false;
        }

        var ext = Path.GetExtension(relPath);
        return ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".yml", StringComparison.OrdinalIgnoreCase);
    }

    public static byte[] CreateSignedZipFromFiles(
        IReadOnlyDictionary<string, string> files,
        string signingKey = DefaultDevSigningKey,
        string? publisher = null)
    {
        var normalizedFiles = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var kvp in files)
        {
            var norm = AppManifestParser.NormalizePath(kvp.Key);
            if (!string.Equals(norm, SignatureEntryName, StringComparison.OrdinalIgnoreCase))
            {
                normalizedFiles[norm] = NormalizeLineEndings(kvp.Value);
            }
        }

        if (!normalizedFiles.ContainsKey(ManifestEntryName))
        {
            throw new AppManifestValidationException($"App package must contain '{ManifestEntryName}' at the root.");
        }

        var contentHash = ComputeCanonicalContentSha256(normalizedFiles);
        var signatureHex = ComputeHmacSha256Hex(contentHash, signingKey);

        var signatureDoc = new AppPackageSignatureDocument(
            Algorithm: "HMAC-SHA256",
            Publisher: publisher ?? "Laftverk",
            ContentSha256: contentHash,
            Signature: signatureHex,
            SignedAt: DateTimeOffset.UtcNow);

        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (entryPath, content) in normalizedFiles)
            {
                var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                writer.Write(content);
            }

            var sigEntry = archive.CreateEntry(SignatureEntryName, CompressionLevel.Optimal);
            using var sigWriter = new StreamWriter(sigEntry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            sigWriter.Write(JsonSerializer.Serialize(signatureDoc));
        }

        return ms.ToArray();
    }

    public static VerifiedAppPackage ReadAndVerifyPackage(
        byte[] zipBytes,
        string signingKey = DefaultDevSigningKey)
    {
        if (zipBytes is null || zipBytes.Length == 0)
        {
            throw new AppPackageSignatureException("App package zip payload is empty.");
        }

        var packageSha256 = Convert.ToHexStringLower(SHA256.HashData(zipBytes));
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string? signatureJson = null;

        try
        {
            using var ms = new MemoryStream(zipBytes, writable: false);
            using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name) && entry.FullName.EndsWith('/'))
                {
                    continue; // Directory entry
                }

                var normPath = AppManifestParser.NormalizePath(entry.FullName);
                using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                var content = reader.ReadToEnd();

                if (string.Equals(normPath, SignatureEntryName, StringComparison.OrdinalIgnoreCase))
                {
                    signatureJson = content;
                }
                else
                {
                    files[normPath] = NormalizeLineEndings(content);
                }
            }
        }
        catch (InvalidDataException ex)
        {
            throw new AppPackageSignatureException($"Invalid zip archive format: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(signatureJson))
        {
            throw new AppPackageSignatureException("App package is missing required 'SIGNATURE' file.");
        }

        AppPackageSignatureDocument? sigDoc;
        try
        {
            sigDoc = JsonSerializer.Deserialize<AppPackageSignatureDocument>(signatureJson);
        }
        catch (Exception ex)
        {
            throw new AppPackageSignatureException($"Malformed 'SIGNATURE' file in app package: {ex.Message}");
        }

        if (sigDoc is null || string.IsNullOrWhiteSpace(sigDoc.Signature) || string.IsNullOrWhiteSpace(sigDoc.ContentSha256))
        {
            throw new AppPackageSignatureException("App package 'SIGNATURE' file is incomplete.");
        }

        var actualContentHash = ComputeCanonicalContentSha256(files);
        if (!string.Equals(actualContentHash, sigDoc.ContentSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new AppPackageSignatureException(
                "App package content SHA-256 does not match the signed digest (package has been tampered with).");
        }

        var expectedSignatureHex = ComputeHmacSha256Hex(actualContentHash, signingKey);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expectedSignatureHex.ToLowerInvariant()),
                Encoding.ASCII.GetBytes(sigDoc.Signature.Trim().ToLowerInvariant())))
        {
            throw new AppPackageSignatureException("App package cryptographic signature verification failed.");
        }

        if (!files.TryGetValue(ManifestEntryName, out var appYaml))
        {
            throw new AppManifestValidationException($"Verified app package is missing '{ManifestEntryName}'.");
        }

        var manifest = AppManifestParser.ParseManifestYaml(appYaml, files);
        return new VerifiedAppPackage(manifest, appYaml, packageSha256, files, zipBytes);
    }

    private static string ComputeCanonicalContentSha256(SortedDictionary<string, string> sortedFiles)
    {
        var sb = new StringBuilder();
        foreach (var (path, content) in sortedFiles)
        {
            sb.Append(path).Append('\n');
            var fileHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
            sb.Append(fileHash).Append('\n');
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static string ComputeHmacSha256Hex(string contentHash, string signingKey)
    {
        var keyBytes = Encoding.UTF8.GetBytes(signingKey);
        var dataBytes = Encoding.UTF8.GetBytes(contentHash);
        var mac = HMACSHA256.HashData(keyBytes, dataBytes);
        return Convert.ToHexStringLower(mac);
    }

    private static string NormalizeLineEndings(string input)
        => input.Replace("\r\n", "\n", StringComparison.Ordinal);
}

/// <summary>
/// Abstraction for storing signed application packages in object storage (S3/MinIO or local volume).
/// </summary>
public interface IAppPackageStorage
{
    Task<string> StorePackageAsync(
        Guid tenantId,
        string appId,
        string version,
        string sha256,
        byte[] zipBytes,
        CancellationToken cancellationToken = default);

    Task<byte[]?> GetPackageAsync(
        string storageKey,
        CancellationToken cancellationToken = default);
}

public sealed class LocalAppPackageStorage : IAppPackageStorage
{
    private readonly string _baseDirectory;

    public LocalAppPackageStorage(string? baseDirectory = null)
    {
        _baseDirectory = baseDirectory
            ?? Path.Combine(Path.GetTempPath(), "laftverk-erp-packages");
        Directory.CreateDirectory(_baseDirectory);
    }

    public async Task<string> StorePackageAsync(
        Guid tenantId,
        string appId,
        string version,
        string sha256,
        byte[] zipBytes,
        CancellationToken cancellationToken = default)
    {
        var safeAppId = appId.Replace('.', '_');
        var storageKey = $"tenants/{tenantId:D}/apps/{safeAppId}/{version}/{sha256}.erpapp.zip";
        var fullPath = Path.Combine(_baseDirectory, storageKey.Replace('/', Path.DirectorySeparatorChar));
        var dir = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(fullPath, zipBytes, cancellationToken);
        return storageKey;
    }

    public async Task<byte[]?> GetPackageAsync(
        string storageKey,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.Combine(_baseDirectory, storageKey.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(fullPath))
        {
            return null;
        }

        return await File.ReadAllBytesAsync(fullPath, cancellationToken);
    }
}

