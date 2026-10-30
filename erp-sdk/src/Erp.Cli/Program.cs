using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Erp.Cli;

public static class ErpDevCli
{
    public const string DefaultSigningKey = "dev-only-app-package-signing-key-2026!";

    public static async Task<int> Main(string[] args)
    {
        var command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
        var appDir = args.Length > 1 ? Path.GetFullPath(args[1]) : Directory.GetCurrentDirectory();
        var coreUrl = Environment.GetEnvironmentVariable("ERP_CORE_URL") ?? "http://localhost:5000";

        switch (command)
        {
            case "dev":
                return await RunDevLoopAsync(appDir, coreUrl);
            case "pack":
            {
                var outPath = args.Length > 2
                    ? Path.GetFullPath(args[2])
                    : Path.Combine(appDir, $"{Path.GetFileName(appDir)}.erpapp.zip");
                var zipBytes = CreateSignedPackageFromDirectory(appDir, DefaultSigningKey);
                await File.WriteAllBytesAsync(outPath, zipBytes);
                Console.WriteLine($"Pakket og signert app til: {outPath} ({zipBytes.Length} bytes)");
                return 0;
            }
            case "validate":
            {
                var manifestPath = Path.Combine(appDir, "app.yaml");
                if (!File.Exists(manifestPath))
                {
                    Console.Error.WriteLine($"Fant ikke app.yaml i {appDir}");
                    return 1;
                }
                Console.WriteLine($"Manifest validert OK: {manifestPath}");
                return 0;
            }
            default:
                Console.WriteLine("Bruk: erp <dev|pack|validate> [app-mappe]");
                return 0;
        }
    }

    public static async Task<bool> SyncDirectoryToCoreAsync(
        string appDirectory,
        HttpClient httpClient,
        string signingKey = DefaultSigningKey,
        CancellationToken cancellationToken = default)
    {
        var zipBytes = CreateSignedPackageFromDirectory(appDirectory, signingKey);
        using var content = new ByteArrayContent(zipBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");

        using var response = await httpClient.PostAsync("/api/v1/apps/install", content, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    public static byte[] CreateSignedPackageFromDirectory(string directoryPath, string signingKey = DefaultSigningKey)
    {
        var manifestPath = Path.Combine(directoryPath, "app.yaml");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Missing app.yaml in '{directoryPath}'.", manifestPath);
        }

        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var filePath in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
        {
            var relPath = Path.GetRelativePath(directoryPath, filePath).Replace('\\', '/');
            if (relPath.StartsWith("bin/", StringComparison.OrdinalIgnoreCase)
                || relPath.StartsWith("obj/", StringComparison.OrdinalIgnoreCase)
                || string.Equals(relPath, "SIGNATURE", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.Equals(relPath, "app.yaml", StringComparison.OrdinalIgnoreCase)
                || relPath.StartsWith("pages/", StringComparison.OrdinalIgnoreCase)
                || relPath.StartsWith("permissions/", StringComparison.OrdinalIgnoreCase)
                || relPath.StartsWith("i18n/", StringComparison.OrdinalIgnoreCase))
            {
                files[relPath] = File.ReadAllBytes(filePath);
            }
        }

        using var canonicalBuffer = new MemoryStream();
        foreach (var (path, content) in files)
        {
            var pathBytes = Encoding.UTF8.GetBytes(path + "\n");
            canonicalBuffer.Write(pathBytes, 0, pathBytes.Length);
            var normalized = NormalizeLfBytes(content);
            canonicalBuffer.Write(normalized, 0, normalized.Length);
            canonicalBuffer.WriteByte((byte)'\n');
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingKey));
        var signatureHex = Convert.ToHexStringLower(hmac.ComputeHash(canonicalBuffer.ToArray()));

        using var ms = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                var entry = archive.CreateEntry(path, System.IO.Compression.CompressionLevel.Optimal);
                using var stream = entry.Open();
                var normalized = NormalizeLfBytes(content);
                stream.Write(normalized, 0, normalized.Length);
            }

            var sigEntry = archive.CreateEntry("SIGNATURE", System.IO.Compression.CompressionLevel.Optimal);
            using (var sigStream = new StreamWriter(sigEntry.Open(), new UTF8Encoding(false)))
            {
                sigStream.Write(signatureHex);
            }
        }

        return ms.ToArray();
    }

    private static async Task<int> RunDevLoopAsync(string appDir, string coreUrl)
    {
        using var http = new HttpClient { BaseAddress = new Uri(coreUrl) };
        using var loginRes = await http.PostAsJsonAsync("/api/v1/auth/dev-login", new { });
        loginRes.EnsureSuccessStatusCode();

        using var loginDoc = JsonDocument.Parse(await loginRes.Content.ReadAsStringAsync());
        var token = loginDoc.RootElement.GetProperty("accessToken").GetString();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SyncDirectoryToCoreAsync(appDir, http);
        Console.WriteLine($"[erp dev] Installerte '{appDir}' mot {coreUrl}. Lytter etter endringer i app.yaml, pages/ og i18n/...");

        using var watcher = new FileSystemWatcher(appDir)
        {
            IncludeSubdirectories = true,
            Filter = "*.yaml",
            EnableRaisingEvents = true
        };

        var tcs = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            tcs.TrySetResult();
        };

        watcher.Changed += async (_, _) =>
        {
            try
            {
                await Task.Delay(150);
                if (await SyncDirectoryToCoreAsync(appDir, http))
                {
                    Console.WriteLine($"[erp dev] Hot-reloadet manifest og sider ({DateTimeOffset.Now:HH:mm:ss}).");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[erp dev] Hot-reload feilet: {ex.Message}");
            }
        };

        await tcs.Task;
        return 0;
    }

    private static byte[] NormalizeLfBytes(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n");
        return Encoding.UTF8.GetBytes(text);
    }
}

