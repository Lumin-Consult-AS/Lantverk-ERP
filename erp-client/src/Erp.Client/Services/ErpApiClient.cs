using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Erp.Client.Models;

namespace Erp.Client.Services;

/// <summary>
/// Pure REST HTTP client for communicating with the ERP Core API (<c>/api/v1/*</c>).
/// Contains no domain-specific logic; all operations are driven by declarative metadata.
/// </summary>
public sealed class ErpApiClient(HttpClient httpClient, I18nService i18n)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public ClientTokenResponse? CurrentAuth { get; private set; }
    public ClientSessionInfo? CurrentSession { get; private set; }
    public Guid ActiveCompanyId => CurrentSession?.Company?.Id ?? CurrentAuth?.CompanyId ?? Guid.Empty;

    public event Action? OnStateChanged;

    public void SetAccessToken(string accessToken, Guid? companyId = null)
    {
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (companyId.HasValue && CurrentAuth is null)
        {
            CurrentAuth = new ClientTokenResponse
            {
                AccessToken = accessToken,
                CompanyId = companyId
            };
        }
    }

    public async Task<ClientTokenResponse?> DevLoginAsync(
        string? subject = null,
        string? tenantSlug = null,
        CancellationToken ct = default)
    {
        using var res = await httpClient.PostAsJsonAsync(
            "/api/v1/auth/dev-login",
            new { subject, tenantSlug },
            JsonOptions,
            ct);

        if (!res.IsSuccessStatusCode)
        {
            return null;
        }

        var token = await res.Content.ReadFromJsonAsync<ClientTokenResponse>(JsonOptions, ct);
        if (token is not null)
        {
            CurrentAuth = token;
            SetAccessToken(token.AccessToken, token.CompanyId);
            await RefreshSessionAndMetadataAsync(ct);
        }

        return token;
    }

    public async Task RefreshSessionAndMetadataAsync(CancellationToken ct = default)
    {
        CurrentSession = await httpClient.GetFromJsonAsync<ClientSessionInfo>("/api/v1/session", JsonOptions, ct);
        var translations = await GetTranslationsAsync(i18n.CurrentLocale, ct);
        i18n.LoadAppTranslations(translations);
        OnStateChanged?.Invoke();
    }

    public async Task<IReadOnlyList<ClientObjectDefinition>> GetObjectsAsync(CancellationToken ct = default)
        => await httpClient.GetFromJsonAsync<List<ClientObjectDefinition>>("/api/v1/metadata/objects", JsonOptions, ct) ?? [];

    public async Task<ClientObjectDefinition?> GetObjectAsync(string objectId, CancellationToken ct = default)
    {
        using var res = await httpClient.GetAsync($"/api/v1/metadata/objects/{Uri.EscapeDataString(objectId)}", ct);
        if (!res.IsSuccessStatusCode) return null;
        return await res.Content.ReadFromJsonAsync<ClientObjectDefinition>(JsonOptions, ct);
    }

    public async Task<IReadOnlyList<ClientEnumDefinition>> GetEnumsAsync(CancellationToken ct = default)
        => await httpClient.GetFromJsonAsync<List<ClientEnumDefinition>>("/api/v1/metadata/enums", JsonOptions, ct) ?? [];

    public async Task<IReadOnlyList<ClientPageDefinition>> GetPagesAsync(CancellationToken ct = default)
        => await httpClient.GetFromJsonAsync<List<ClientPageDefinition>>("/api/v1/metadata/pages", JsonOptions, ct) ?? [];

    public async Task<ClientPageDefinition?> GetPageAsync(string pageId, CancellationToken ct = default)
    {
        using var res = await httpClient.GetAsync($"/api/v1/metadata/pages/{Uri.EscapeDataString(pageId)}", ct);
        if (!res.IsSuccessStatusCode) return null;
        return await res.Content.ReadFromJsonAsync<ClientPageDefinition>(JsonOptions, ct);
    }

    public async Task<IReadOnlyList<ClientPermissionSet>> GetPermissionsAsync(CancellationToken ct = default)
        => await httpClient.GetFromJsonAsync<List<ClientPermissionSet>>("/api/v1/metadata/permissions", JsonOptions, ct) ?? [];

    public async Task<IReadOnlyDictionary<string, string>> GetTranslationsAsync(string locale, CancellationToken ct = default)
        => await httpClient.GetFromJsonAsync<Dictionary<string, string>>(
               $"/api/v1/metadata/i18n/{Uri.EscapeDataString(locale)}",
               JsonOptions,
               ct)
           ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public async Task SwitchLocaleAsync(string locale, CancellationToken ct = default)
    {
        var dict = await GetTranslationsAsync(locale, ct);
        i18n.SetLocale(locale, dict);
        OnStateChanged?.Invoke();
    }

    public async Task<bool> InstallBuiltInAppAsync(string appId, CancellationToken ct = default)
    {
        using var res = await httpClient.PostAsync($"/api/v1/apps/install-builtin/{Uri.EscapeDataString(appId)}", null, ct);
        if (!res.IsSuccessStatusCode) return false;
        await RefreshSessionAndMetadataAsync(ct);
        return true;
    }

    public async Task<bool> UninstallAppAsync(string appId, CancellationToken ct = default)
    {
        using var res = await httpClient.DeleteAsync($"/api/v1/apps/{Uri.EscapeDataString(appId)}", ct);
        if (!res.IsSuccessStatusCode) return false;
        await RefreshSessionAndMetadataAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<ClientRecordData>> QueryRecordsAsync(
        string objectId,
        string? filter = null,
        string? orderBy = null,
        string? select = null,
        int top = 50,
        int skip = 0,
        Guid? companyId = null,
        CancellationToken ct = default)
    {
        var comp = FormatCompanySegment(companyId);
        var queryParams = new List<string>
        {
            $"top={top.ToString(CultureInfo.InvariantCulture)}",
            $"skip={skip.ToString(CultureInfo.InvariantCulture)}"
        };

        if (!string.IsNullOrWhiteSpace(filter))
        {
            queryParams.Add($"filter={Uri.EscapeDataString(filter)}");
        }

        if (!string.IsNullOrWhiteSpace(orderBy))
        {
            queryParams.Add($"orderBy={Uri.EscapeDataString(orderBy)}");
        }

        if (!string.IsNullOrWhiteSpace(select))
        {
            queryParams.Add($"select={Uri.EscapeDataString(select)}");
        }

        var url = $"/api/v1/{comp}/{Uri.EscapeDataString(objectId)}?{string.Join("&", queryParams)}";
        return await httpClient.GetFromJsonAsync<List<ClientRecordData>>(url, JsonOptions, ct) ?? [];
    }

    public async Task<ClientRecordData?> GetRecordAsync(
        string objectId,
        Guid id,
        Guid? companyId = null,
        CancellationToken ct = default)
    {
        var comp = FormatCompanySegment(companyId);
        using var res = await httpClient.GetAsync($"/api/v1/{comp}/{Uri.EscapeDataString(objectId)}/{id:D}", ct);
        if (!res.IsSuccessStatusCode) return null;
        return await res.Content.ReadFromJsonAsync<ClientRecordData>(JsonOptions, ct);
    }

    public async Task<ClientRecordData> CreateRecordAsync(
        string objectId,
        IDictionary<string, object?> fields,
        Guid? companyId = null,
        CancellationToken ct = default)
    {
        var comp = FormatCompanySegment(companyId);
        using var res = await httpClient.PostAsJsonAsync(
            $"/api/v1/{comp}/{Uri.EscapeDataString(objectId)}",
            fields,
            JsonOptions,
            ct);

        await EnsureSuccessOrThrowReadableAsync(res, ct);
        return (await res.Content.ReadFromJsonAsync<ClientRecordData>(JsonOptions, ct))!;
    }

    public async Task<ClientRecordData> UpdateRecordAsync(
        string objectId,
        Guid id,
        long rowVersion,
        IDictionary<string, object?> fields,
        Guid? companyId = null,
        CancellationToken ct = default)
    {
        var comp = FormatCompanySegment(companyId);
        using var req = new HttpRequestMessage(
            HttpMethod.Patch,
            $"/api/v1/{comp}/{Uri.EscapeDataString(objectId)}/{id:D}");
        req.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{rowVersion}\""));
        req.Content = JsonContent.Create(fields, options: JsonOptions);

        using var res = await httpClient.SendAsync(req, ct);
        await EnsureSuccessOrThrowReadableAsync(res, ct);
        return (await res.Content.ReadFromJsonAsync<ClientRecordData>(JsonOptions, ct))!;
    }

    public async Task DeleteRecordAsync(
        string objectId,
        Guid id,
        long rowVersion,
        Guid? companyId = null,
        CancellationToken ct = default)
    {
        var comp = FormatCompanySegment(companyId);
        using var req = new HttpRequestMessage(
            HttpMethod.Delete,
            $"/api/v1/{comp}/{Uri.EscapeDataString(objectId)}/{id:D}");
        req.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{rowVersion}\""));

        using var res = await httpClient.SendAsync(req, ct);
        await EnsureSuccessOrThrowReadableAsync(res, ct);
    }

    public async Task<ClientRecordData> ExecuteTransitionAsync(
        string objectId,
        Guid id,
        string transition,
        long rowVersion,
        Guid? companyId = null,
        CancellationToken ct = default)
    {
        var comp = FormatCompanySegment(companyId);
        using var req = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/v1/{comp}/{Uri.EscapeDataString(objectId)}/{id:D}/transitions/{Uri.EscapeDataString(transition)}");
        req.Headers.IfMatch.Add(new EntityTagHeaderValue($"\"{rowVersion}\""));

        using var res = await httpClient.SendAsync(req, ct);
        await EnsureSuccessOrThrowReadableAsync(res, ct);
        return (await res.Content.ReadFromJsonAsync<ClientRecordData>(JsonOptions, ct))!;
    }

    public async Task ExecuteBatchAsync(
        IReadOnlyList<ClientBatchOperation> operations,
        Guid? companyId = null,
        CancellationToken ct = default)
    {
        var comp = FormatCompanySegment(companyId);
        using var res = await httpClient.PostAsJsonAsync(
            $"/api/v1/{comp}/$batch",
            operations,
            JsonOptions,
            ct);
        await EnsureSuccessOrThrowReadableAsync(res, ct);
    }

    public static string? BuildSearchAndCustomFilter(
        ClientObjectDefinition? objectDef,
        string? searchText,
        string? customFilter)
    {
        string? searchExpr = null;
        if (!string.IsNullOrWhiteSpace(searchText) && objectDef is not null)
        {
            var safeTerm = searchText.Trim().Replace("'", "''", StringComparison.Ordinal);
            var stringFields = objectDef.Fields
                .Where(f => f.Type is ClientFieldType.String or ClientFieldType.Text)
                .Select(f => $"{f.GetExposedName(objectDef.OwnerApp)} contains '{safeTerm}'")
                .ToList();

            if (stringFields.Count > 0)
            {
                searchExpr = stringFields.Count == 1
                    ? stringFields[0]
                    : $"({string.Join(" or ", stringFields)})";
            }
        }

        if (!string.IsNullOrWhiteSpace(searchExpr) && !string.IsNullOrWhiteSpace(customFilter))
        {
            return $"{searchExpr} and ({customFilter.Trim()})";
        }

        return !string.IsNullOrWhiteSpace(searchExpr)
            ? searchExpr
            : string.IsNullOrWhiteSpace(customFilter) ? null : customFilter.Trim();
    }

    private string FormatCompanySegment(Guid? companyId)
    {
        var id = companyId ?? ActiveCompanyId;
        return id != Guid.Empty ? id.ToString("D") : "default";
    }

    private static async Task EnsureSuccessOrThrowReadableAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var rawBody = await response.Content.ReadAsStringAsync(ct);
        if (!string.IsNullOrWhiteSpace(rawBody))
        {
            try
            {
                using var doc = JsonDocument.Parse(rawBody);
                if (doc.RootElement.TryGetProperty("error", out var errProp) && errProp.ValueKind == JsonValueKind.String)
                {
                    throw new InvalidOperationException(errProp.GetString());
                }
            }
            catch (JsonException)
            {
                // Ignore JSON parse errors and fall back to status code message
            }
        }

        response.EnsureSuccessStatusCode();
    }
}

