using System.Data.Common;
using System.Security.Cryptography;
using Erp.Core.Domain.Data;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Data;
using Erp.Core.Infrastructure.Events;
using Erp.Core.Infrastructure.Persistence;
using Erp.Core.Infrastructure.Storage;
using Npgsql;
using NpgsqlTypes;

namespace Erp.Core.Infrastructure.Documents;

public sealed class DocumentOptions
{
    public const string SectionName = "Documents";

    public long MaxFileBytes { get; set; } = 25 * 1024 * 1024;

    /// <summary>Retention in years per category when a country has no specific rule. Vouchers are kept 5 years.</summary>
    public Dictionary<string, int> DefaultRetentionYears { get; set; } = new(StringComparer.OrdinalIgnoreCase) { ["bilag"] = 5 };

    /// <summary>Retention overrides per country (ISO code) and category, e.g. <c>Documents:Retention:SE:bilag = 7</c>.</summary>
    public Dictionary<string, Dictionary<string, int>> Retention { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Domain of generated inbox addresses, e.g. <c>innboks.example.no</c>.</summary>
    public string InboxDomain { get; set; } = "inbox.localhost";

    public int? RetentionYears(string? countryCode, string category)
    {
        if (countryCode is not null
            && Retention.TryGetValue(countryCode, out var perCountry)
            && perCountry.TryGetValue(category, out var years))
        {
            return years;
        }

        return DefaultRetentionYears.TryGetValue(category, out var defaultYears) ? defaultYears : null;
    }
}

public sealed record StoredFile(
    Guid Id,
    Guid? CompanyId,
    string FileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string Source,
    string ScanStatus,
    string? ScanSignature,
    DateTimeOffset? RetainUntil,
    DateTimeOffset CreatedAt)
{
    public bool IsAvailable => ScanStatus == "clean";
}

public sealed record StoreFileResult(StoredFile File, bool Duplicate);

public sealed record AttachmentInfo(
    Guid Id,
    Guid FileId,
    string ObjectId,
    Guid RecordId,
    string Category,
    DateTimeOffset? RetainUntil,
    DateTimeOffset CreatedAt);

/// <summary>The file or attachment is inside its retention period and cannot be removed.</summary>
public sealed class RetentionLockedException(string message) : Exception(message);

/// <summary>The file is not (yet) available, e.g. because it has not been virus scanned or was found infected.</summary>
public sealed class FileUnavailableException(string message) : Exception(message);

/// <summary>
/// Owns files and attachments (ADR-0012): content goes to the object store, metadata to <c>core.file</c>. Content is
/// virus scanned before it becomes available, identical content is stored once per company, and files attached in a
/// category with a retention period are locked in the object store until the period has passed.
/// </summary>
public sealed class FileService(
    CoreDbContext dbContext,
    ITenantContext tenantContext,
    IObjectStorage storage,
    IVirusScanner scanner,
    DocumentOptions options,
    RecordDataService recordData)
{
    internal const string FileColumns =
        "id, company_id, file_name, content_type, size_bytes, sha256, source, scan_status, scan_signature, retain_until, created_at";

    /// <summary>
    /// Stores a file. Identical content in the same company is not stored again (the existing file is returned with
    /// <c>Duplicate = true</c>). <paramref name="trusted"/> skips the scan for content the platform generated itself.
    /// </summary>
    public async Task<StoreFileResult> StoreAsync(
        string fileName,
        string? contentType,
        byte[] content,
        Guid? companyId,
        string source,
        bool trusted,
        CancellationToken cancellationToken)
    {
        if (content.Length == 0)
        {
            throw new RecordValidationException("The file is empty.", "file");
        }

        if (content.Length > options.MaxFileBytes)
        {
            throw new RecordValidationException($"The file exceeds the maximum size of {options.MaxFileBytes / (1024 * 1024)} MB.", "file");
        }

        var tenantId = tenantContext.TenantId ?? throw new InvalidOperationException("Files require a tenant context.");
        var userId = tenantContext.UserId ?? tenantId;
        var effectiveCompany = companyId ?? tenantContext.CompanyId;
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);

        var existing = await FindDuplicateAsync(connection, effectiveCompany, sha256, cancellationToken);
        if (existing is not null)
        {
            return new StoreFileResult(existing, true);
        }

        var fileId = Guid.NewGuid();
        var key = $"tenants/{tenantId:D}/files/{fileId:D}";
        var scan = trusted ? new ScanResult(ScanOutcome.Clean) : await scanner.ScanAsync(content, cancellationToken);

        // Infected content is never written to the object store; only the finding is recorded.
        if (scan.Outcome != ScanOutcome.Infected)
        {
            await storage.PutAsync(key, content, string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType, cancellationToken);
        }

        try
        {
            await using var cmd = TenantDb.Create(
                connection,
                null,
                $"""
                INSERT INTO core.file
                    (id, tenant_id, company_id, created_by, updated_by, file_name, content_type, size_bytes, sha256, storage_key,
                     source, scan_status, scan_signature)
                VALUES
                    (@id, @tenant, @company, @user, @user, @name, @type, @size, @sha, @key, @source, @status, @signature)
                RETURNING {FileColumns};
                """);
            TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, fileId);
            TenantDb.Add(cmd, "tenant", NpgsqlDbType.Uuid, tenantId);
            TenantDb.Add(cmd, "company", NpgsqlDbType.Uuid, effectiveCompany);
            TenantDb.Add(cmd, "user", NpgsqlDbType.Uuid, userId);
            TenantDb.Add(cmd, "name", NpgsqlDbType.Varchar, CleanFileName(fileName));
            TenantDb.Add(cmd, "type", NpgsqlDbType.Varchar, string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
            TenantDb.Add(cmd, "size", NpgsqlDbType.Bigint, (long)content.Length);
            TenantDb.Add(cmd, "sha", NpgsqlDbType.Varchar, sha256);
            TenantDb.Add(cmd, "key", NpgsqlDbType.Varchar, key);
            TenantDb.Add(cmd, "source", NpgsqlDbType.Varchar, source);
            TenantDb.Add(cmd, "status", NpgsqlDbType.Varchar, scan.Outcome switch
            {
                ScanOutcome.Clean => "clean",
                ScanOutcome.Infected => "infected",
                _ => "pending"
            });
            TenantDb.Add(cmd, "signature", NpgsqlDbType.Varchar, scan.Signature);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            return new StoreFileResult(ReadFile(reader), false);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            // A concurrent upload of the same content won; use its file and remove the object we just wrote.
            await storage.DeleteAsync(key, cancellationToken);
            var winner = await FindDuplicateAsync(connection, effectiveCompany, sha256, cancellationToken)
                ?? throw new InvalidOperationException("Duplicate file disappeared.");
            return new StoreFileResult(winner, true);
        }
    }

    public async Task<StoredFile?> GetAsync(Guid fileId, CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        return await GetAsync(connection, fileId, cancellationToken);
    }

    public async Task<IReadOnlyList<StoredFile>> GetManyAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(connection, null, $"SELECT {FileColumns} FROM core.file WHERE id = ANY (@ids);");
        TenantDb.Add(cmd, "ids", NpgsqlDbType.Array | NpgsqlDbType.Uuid, ids.ToArray());
        var result = new List<StoredFile>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadFile(reader));
        }

        return result;
    }

    /// <summary>The content of a file that has passed the virus scan.</summary>
    public async Task<(StoredFile File, byte[] Content)> ReadContentAsync(Guid fileId, CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        var file = await GetAsync(connection, fileId, cancellationToken)
            ?? throw new KeyNotFoundException($"File '{fileId}' was not found.");
        if (!file.IsAvailable)
        {
            throw new FileUnavailableException(
                file.ScanStatus == "infected"
                    ? $"File '{file.FileName}' was found infected ({file.ScanSignature}) and is not available."
                    : $"File '{file.FileName}' has not been virus scanned yet and is not available.");
        }

        var content = await storage.GetAsync(await StorageKeyAsync(connection, fileId, cancellationToken), cancellationToken)
            ?? throw new KeyNotFoundException($"The content of file '{fileId}' is missing from the object store.");
        return (file, content);
    }

    /// <summary>Scans a file that was stored while the scanner was unavailable. Returns the file's status afterwards.</summary>
    public async Task<string> RescanAsync(Guid fileId, CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        var file = await GetAsync(connection, fileId, cancellationToken);
        if (file is null || file.ScanStatus != "pending")
        {
            return file?.ScanStatus ?? "missing";
        }

        var content = await storage.GetAsync(await StorageKeyAsync(connection, fileId, cancellationToken), cancellationToken);
        var result = content is null
            ? new ScanResult(ScanOutcome.Unavailable, Error: "Content missing from the object store.")
            : await scanner.ScanAsync(content, cancellationToken);

        await using var cmd = TenantDb.Create(
            connection,
            null,
            """
            UPDATE core.file
               SET scan_status = @status, scan_signature = @signature, scan_attempts = scan_attempts + 1,
                   next_scan_at = now() + make_interval(mins => LEAST(60, 1 << LEAST(scan_attempts, 6))),
                   updated_at = now(), row_version = row_version + 1
             WHERE id = @id AND scan_status = 'pending'
            RETURNING scan_status;
            """);
        TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, fileId);
        TenantDb.Add(cmd, "status", NpgsqlDbType.Varchar, result.Outcome switch
        {
            ScanOutcome.Clean => "clean",
            ScanOutcome.Infected => "infected",
            _ => "pending"
        });
        TenantDb.Add(cmd, "signature", NpgsqlDbType.Varchar, result.Signature);
        var status = (await cmd.ExecuteScalarAsync(cancellationToken)) as string ?? "pending";

        // An infected file must not remain in the object store.
        if (status == "infected")
        {
            await storage.DeleteAsync(await StorageKeyAsync(connection, fileId, cancellationToken), cancellationToken);
        }

        return status;
    }

    // ------------------------------------------------------------------ attachments

    /// <summary>
    /// Links a file to a record. The caller must be allowed to read the record. When the category has a retention
    /// period (e.g. vouchers, 5 years) the file is locked in the object store until it has passed.
    /// </summary>
    public async Task<AttachmentInfo> AttachAsync(
        Guid fileId,
        string objectId,
        Guid recordId,
        string category,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            throw new RecordValidationException("Attachment category is required.", "category");
        }

        category = category.Trim().ToLowerInvariant();
        var tenantId = tenantContext.TenantId ?? throw new InvalidOperationException("Attachments require a tenant context.");
        var userId = tenantContext.UserId ?? tenantId;

        // Reading the record enforces that the user may see the object they attach to (and that it exists).
        _ = await recordData.GetAsync(objectId, recordId, null, cancellationToken)
            ?? throw new KeyNotFoundException($"Record '{recordId}' in '{objectId}' was not found.");

        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        var file = await GetAsync(connection, fileId, cancellationToken)
            ?? throw new KeyNotFoundException($"File '{fileId}' was not found.");
        if (!file.IsAvailable)
        {
            throw new FileUnavailableException($"File '{file.FileName}' is not available and cannot be attached.");
        }

        var years = options.RetentionYears(await CountryCodeAsync(connection, file.CompanyId, cancellationToken), category);
        var retainUntil = years is null ? (DateTimeOffset?)null : DateTimeOffset.UtcNow.AddYears(years.Value);

        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        AttachmentInfo? attachment;
        await using (var cmd = TenantDb.Create(
            connection,
            tx,
            """
            INSERT INTO core.attachment (id, tenant_id, company_id, created_by, updated_by, file_id, object_id, record_id, category, retain_until)
            VALUES (@id, @tenant, @company, @user, @user, @file, @object, @record, @category, @retain)
            ON CONFLICT (tenant_id, file_id, object_id, record_id, category) DO NOTHING
            RETURNING id, file_id, object_id, record_id, category, retain_until, created_at;
            """))
        {
            TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, Guid.NewGuid());
            TenantDb.Add(cmd, "tenant", NpgsqlDbType.Uuid, tenantId);
            TenantDb.Add(cmd, "company", NpgsqlDbType.Uuid, file.CompanyId);
            TenantDb.Add(cmd, "user", NpgsqlDbType.Uuid, userId);
            TenantDb.Add(cmd, "file", NpgsqlDbType.Uuid, fileId);
            TenantDb.Add(cmd, "object", NpgsqlDbType.Varchar, objectId);
            TenantDb.Add(cmd, "record", NpgsqlDbType.Uuid, recordId);
            TenantDb.Add(cmd, "category", NpgsqlDbType.Varchar, category);
            TenantDb.Add(cmd, "retain", NpgsqlDbType.TimestampTz, retainUntil);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            attachment = await reader.ReadAsync(cancellationToken) ? ReadAttachment(reader) : null;
        }

        if (attachment is null)
        {
            await tx.RollbackAsync(cancellationToken);
            return await FindAttachmentAsync(connection, fileId, objectId, recordId, category, cancellationToken)
                ?? throw new InvalidOperationException("Attachment disappeared.");
        }

        if (retainUntil is not null)
        {
            // Lock first: if the object store cannot enforce retention the attachment is not created.
            await storage.LockUntilAsync(await StorageKeyAsync(connection, fileId, cancellationToken, tx), retainUntil.Value, cancellationToken);

            await using var update = TenantDb.Create(
                connection,
                tx,
                """
                UPDATE core.file
                   SET retain_until = GREATEST(COALESCE(retain_until, @until), @until), updated_at = now(), row_version = row_version + 1
                 WHERE id = @id;
                """);
            TenantDb.Add(update, "id", NpgsqlDbType.Uuid, fileId);
            TenantDb.Add(update, "until", NpgsqlDbType.TimestampTz, retainUntil);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return attachment;
    }

    public async Task<IReadOnlyList<AttachmentInfo>> ListAttachmentsAsync(
        string objectId,
        Guid recordId,
        CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(
            connection,
            null,
            """
            SELECT id, file_id, object_id, record_id, category, retain_until, created_at
              FROM core.attachment WHERE object_id = @object AND record_id = @record ORDER BY created_at;
            """);
        TenantDb.Add(cmd, "object", NpgsqlDbType.Varchar, objectId);
        TenantDb.Add(cmd, "record", NpgsqlDbType.Uuid, recordId);
        var result = new List<AttachmentInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadAttachment(reader));
        }

        return result;
    }

    /// <summary>Removes a link. Refused with <see cref="RetentionLockedException"/> while the attachment's retention applies.</summary>
    public async Task DetachAsync(Guid attachmentId, CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken);

        DateTimeOffset? retainUntil;
        await using (var select = TenantDb.Create(connection, tx, "SELECT retain_until FROM core.attachment WHERE id = @id FOR UPDATE;"))
        {
            TenantDb.Add(select, "id", NpgsqlDbType.Uuid, attachmentId);
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new KeyNotFoundException($"Attachment '{attachmentId}' was not found.");
            }

            retainUntil = reader.IsDBNull(0) ? null : reader.GetFieldValue<DateTimeOffset>(0);
        }

        if (retainUntil > DateTimeOffset.UtcNow)
        {
            throw new RetentionLockedException(
                $"The attachment is under retention until {retainUntil:yyyy-MM-dd} and cannot be removed.");
        }

        await using var delete = TenantDb.Create(connection, tx, "DELETE FROM core.attachment WHERE id = @id;");
        TenantDb.Add(delete, "id", NpgsqlDbType.Uuid, attachmentId);
        await delete.ExecuteNonQueryAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    /// <summary>Deletes a file that is neither attached nor under retention. The object store enforces the same rule.</summary>
    public async Task DeleteFileAsync(Guid fileId, CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        var file = await GetAsync(connection, fileId, cancellationToken)
            ?? throw new KeyNotFoundException($"File '{fileId}' was not found.");

        if (file.RetainUntil > DateTimeOffset.UtcNow)
        {
            throw new RetentionLockedException($"The file is under retention until {file.RetainUntil:yyyy-MM-dd} and cannot be deleted.");
        }

        await using (var count = TenantDb.Create(connection, null, "SELECT count(*) FROM core.attachment WHERE file_id = @id;"))
        {
            TenantDb.Add(count, "id", NpgsqlDbType.Uuid, fileId);
            if (Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken)) > 0)
            {
                throw new RetentionLockedException("The file is attached to a record; remove the attachments first.");
            }
        }

        try
        {
            await storage.DeleteAsync(await StorageKeyAsync(connection, fileId, cancellationToken), cancellationToken);
        }
        catch (ObjectLockedException ex)
        {
            throw new RetentionLockedException(ex.Message);
        }

        await using var delete = TenantDb.Create(connection, null, "DELETE FROM core.file WHERE id = @id;");
        TenantDb.Add(delete, "id", NpgsqlDbType.Uuid, fileId);
        await delete.ExecuteNonQueryAsync(cancellationToken);
    }

    // ------------------------------------------------------------------ internals

    private static async Task<StoredFile?> GetAsync(DbConnection connection, Guid fileId, CancellationToken cancellationToken)
    {
        await using var cmd = TenantDb.Create(connection, null, $"SELECT {FileColumns} FROM core.file WHERE id = @id;");
        TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, fileId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadFile(reader) : null;
    }

    private static async Task<StoredFile?> FindDuplicateAsync(
        DbConnection connection,
        Guid? companyId,
        string sha256,
        CancellationToken cancellationToken)
    {
        await using var cmd = TenantDb.Create(
            connection,
            null,
            $"""
            SELECT {FileColumns} FROM core.file
             WHERE COALESCE(company_id, '00000000-0000-0000-0000-000000000000'::uuid) = COALESCE(@company, '00000000-0000-0000-0000-000000000000'::uuid)
               AND sha256 = @sha AND scan_status <> 'infected';
            """);
        TenantDb.Add(cmd, "company", NpgsqlDbType.Uuid, companyId);
        TenantDb.Add(cmd, "sha", NpgsqlDbType.Varchar, sha256);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadFile(reader) : null;
    }

    private static async Task<AttachmentInfo?> FindAttachmentAsync(
        DbConnection connection,
        Guid fileId,
        string objectId,
        Guid recordId,
        string category,
        CancellationToken cancellationToken)
    {
        await using var cmd = TenantDb.Create(
            connection,
            null,
            """
            SELECT id, file_id, object_id, record_id, category, retain_until, created_at FROM core.attachment
             WHERE file_id = @file AND object_id = @object AND record_id = @record AND category = @category;
            """);
        TenantDb.Add(cmd, "file", NpgsqlDbType.Uuid, fileId);
        TenantDb.Add(cmd, "object", NpgsqlDbType.Varchar, objectId);
        TenantDb.Add(cmd, "record", NpgsqlDbType.Uuid, recordId);
        TenantDb.Add(cmd, "category", NpgsqlDbType.Varchar, category);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadAttachment(reader) : null;
    }

    private static async Task<string> StorageKeyAsync(
        DbConnection connection,
        Guid fileId,
        CancellationToken cancellationToken,
        DbTransaction? tx = null)
    {
        await using var cmd = TenantDb.Create(connection, tx, "SELECT storage_key FROM core.file WHERE id = @id;");
        TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, fileId);
        return (string)(await cmd.ExecuteScalarAsync(cancellationToken)
            ?? throw new KeyNotFoundException($"File '{fileId}' was not found."));
    }

    /// <summary>Country of the file's company (retention rules differ per country); the tenant's default country otherwise.</summary>
    private async Task<string?> CountryCodeAsync(DbConnection connection, Guid? companyId, CancellationToken cancellationToken)
    {
        await using var cmd = TenantDb.Create(
            connection,
            null,
            """
            SELECT COALESCE(
                (SELECT c.iso_code FROM core.company co JOIN core.country c ON c.id = co.country_id WHERE co.id = @company),
                (SELECT default_country_code FROM core.tenant LIMIT 1));
            """);
        TenantDb.Add(cmd, "company", NpgsqlDbType.Uuid, companyId);
        return await cmd.ExecuteScalarAsync(cancellationToken) as string;
    }

    internal static StoredFile ReadFile(DbDataReader r) => new(
        r.GetGuid(0),
        r.IsDBNull(1) ? null : r.GetGuid(1),
        r.GetString(2),
        r.GetString(3),
        r.GetInt64(4),
        r.GetString(5),
        r.GetString(6),
        r.GetString(7),
        r.IsDBNull(8) ? null : r.GetString(8),
        r.IsDBNull(9) ? null : r.GetFieldValue<DateTimeOffset>(9),
        r.GetFieldValue<DateTimeOffset>(10));

    private static AttachmentInfo ReadAttachment(DbDataReader r) => new(
        r.GetGuid(0),
        r.GetGuid(1),
        r.GetString(2),
        r.GetGuid(3),
        r.GetString(4),
        r.IsDBNull(5) ? null : r.GetFieldValue<DateTimeOffset>(5),
        r.GetFieldValue<DateTimeOffset>(6));

    private static string CleanFileName(string fileName)
    {
        var name = Path.GetFileName(fileName.Replace('\\', '/'));
        name = string.IsNullOrWhiteSpace(name) ? "file" : name.Trim();
        return name.Length > 300 ? name[..300] : name;
    }
}
