using System.Data.Common;
using Erp.Core.Domain.Data;
using Erp.Core.Domain.Tenancy;
using Erp.Core.Infrastructure.Events;
using Erp.Core.Infrastructure.Persistence;
using NpgsqlTypes;

namespace Erp.Core.Infrastructure.Documents;

public sealed record InboxInfo(Guid Id, Guid? CompanyId, string Channel, string Name, string? Address, bool IsActive, DateTimeOffset CreatedAt);

public sealed record InboxItemInfo(
    Guid Id,
    Guid InboxId,
    Guid? CompanyId,
    string Channel,
    string? MessageId,
    string Sender,
    string? SenderName,
    string Subject,
    string? BodyText,
    DateTimeOffset ReceivedAt,
    string Status,
    IReadOnlyList<Guid> FileIds,
    IReadOnlyList<Guid> DuplicateFileIds,
    string? ClaimedBy,
    DateTimeOffset? ClaimedAt,
    DateTimeOffset? ProcessedAt,
    string? ResultObjectId,
    Guid? ResultRecordId,
    string? RejectReason);

public sealed record IngestAttachment(string FileName, string? ContentType, byte[] Content);

/// <summary>A document arriving in an inbox, whatever the channel: e-mail webhook, upload or API.</summary>
public sealed record IngestItem(
    string Channel,
    string? MessageId,
    string Sender,
    string? SenderName,
    string Subject,
    string? BodyText,
    DateTimeOffset ReceivedAt,
    IReadOnlyList<IngestAttachment> Attachments);

/// <summary>
/// Inboxes and the documents arriving in them (ADR-0012). An item is created for every incoming document; once its
/// files have passed the virus scan it is <c>new</c> and <c>core.inbox.received@1</c> is published in the same
/// transaction. Apps (or users) claim items; items nobody claims form the manual worklist.
/// </summary>
public sealed class InboxService(
    CoreDbContext dbContext,
    ITenantContext tenantContext,
    FileService files,
    EventPublisher eventPublisher,
    DocumentOptions options,
    Erp.Core.Infrastructure.Attention.AttentionCache attentionCache)
{
    private const string ItemColumns =
        "id, inbox_id, company_id, channel, message_id, sender, sender_name, subject, body_text, received_at, status, file_ids, " +
        "duplicate_file_ids, claimed_by, claimed_at, processed_at, result_object_id, result_record_id, reject_reason";

    // ------------------------------------------------------------------ inboxes

    public async Task<InboxInfo> CreateInboxAsync(string channel, string name, Guid? companyId, CancellationToken cancellationToken)
    {
        channel = (channel ?? string.Empty).Trim().ToLowerInvariant();
        if (channel is not ("email" or "upload" or "api"))
        {
            throw new RecordValidationException("Inbox channel must be 'email', 'upload' or 'api'.", "channel");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new RecordValidationException("Inbox name is required.", "name");
        }

        var tenantId = tenantContext.TenantId ?? throw new InvalidOperationException("Inboxes require a tenant context.");
        var userId = tenantContext.UserId ?? tenantId;
        var effectiveCompany = companyId ?? tenantContext.CompanyId;
        var address = channel == "email" ? $"inbox-{Guid.NewGuid().ToString("N")[..10]}@{options.InboxDomain}" : null;

        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(
            connection,
            null,
            """
            INSERT INTO core.inbox (id, tenant_id, company_id, created_by, updated_by, channel, name, address)
            VALUES (@id, @tenant, @company, @user, @user, @channel, @name, @address)
            RETURNING id, company_id, channel, name, address, is_active, created_at;
            """);
        TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, Guid.NewGuid());
        TenantDb.Add(cmd, "tenant", NpgsqlDbType.Uuid, tenantId);
        TenantDb.Add(cmd, "company", NpgsqlDbType.Uuid, effectiveCompany);
        TenantDb.Add(cmd, "user", NpgsqlDbType.Uuid, userId);
        TenantDb.Add(cmd, "channel", NpgsqlDbType.Varchar, channel);
        TenantDb.Add(cmd, "name", NpgsqlDbType.Varchar, name.Trim());
        TenantDb.Add(cmd, "address", NpgsqlDbType.Varchar, address);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadInbox(reader);
    }

    public async Task<IReadOnlyList<InboxInfo>> ListInboxesAsync(CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(
            connection, null, "SELECT id, company_id, channel, name, address, is_active, created_at FROM core.inbox ORDER BY created_at;");
        var result = new List<InboxInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadInbox(reader));
        }

        return result;
    }

    public async Task<InboxInfo?> GetInboxAsync(Guid inboxId, CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(
            connection, null, "SELECT id, company_id, channel, name, address, is_active, created_at FROM core.inbox WHERE id = @id;");
        TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, inboxId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadInbox(reader) : null;
    }

    // ------------------------------------------------------------------ receiving

    /// <summary>
    /// Stores the item's files (scanned, deduplicated) and creates the inbox item. Idempotent on <c>MessageId</c>, so a
    /// provider that redelivers a webhook does not create a second item.
    /// </summary>
    public async Task<InboxItemInfo> IngestAsync(Guid inboxId, IngestItem item, CancellationToken cancellationToken)
    {
        var inbox = await GetInboxAsync(inboxId, cancellationToken)
            ?? throw new KeyNotFoundException($"Inbox '{inboxId}' was not found.");
        if (!inbox.IsActive)
        {
            throw new RecordValidationException("The inbox is inactive.", "inbox");
        }

        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        if (item.MessageId is not null
            && await FindByMessageIdAsync(connection, inboxId, item.MessageId, cancellationToken) is { } existing)
        {
            return existing;
        }

        var fileIds = new List<Guid>();
        var duplicates = new List<Guid>();
        var infected = new List<string>();
        var pending = false;
        foreach (var attachment in item.Attachments)
        {
            var stored = await files.StoreAsync(
                attachment.FileName, attachment.ContentType, attachment.Content, inbox.CompanyId, "inbox", trusted: false, cancellationToken);
            if (stored.File.ScanStatus == "infected")
            {
                infected.Add($"{stored.File.FileName} ({stored.File.ScanSignature})");
                continue;
            }

            if (!fileIds.Contains(stored.File.Id))
            {
                fileIds.Add(stored.File.Id);
            }

            if (stored.Duplicate)
            {
                duplicates.Add(stored.File.Id);
            }

            pending |= stored.File.ScanStatus == "pending";
        }

        var rejectReason = infected.Count > 0 ? $"Virus found in attachment: {string.Join(", ", infected)}." : null;
        var status = rejectReason is not null ? "rejected" : pending ? "scanning" : "new";

        var tenantId = tenantContext.TenantId!.Value;
        var userId = tenantContext.UserId ?? tenantId;
        var itemId = Guid.NewGuid();
        var correlationId = Guid.NewGuid().ToString("D");

        await using var tx = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var cmd = TenantDb.Create(
                connection,
                tx,
                """
                INSERT INTO core.inboxitem
                    (id, tenant_id, company_id, created_by, updated_by, inbox_id, channel, message_id, sender, sender_name, subject,
                     body_text, received_at, status, file_ids, duplicate_file_ids, reject_reason)
                VALUES
                    (@id, @tenant, @company, @user, @user, @inbox, @channel, @messageId, @sender, @senderName, @subject,
                     @body, @receivedAt, @status, @fileIds, @duplicateIds, @reject);
                """))
            {
                TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, itemId);
                TenantDb.Add(cmd, "tenant", NpgsqlDbType.Uuid, tenantId);
                TenantDb.Add(cmd, "company", NpgsqlDbType.Uuid, inbox.CompanyId);
                TenantDb.Add(cmd, "user", NpgsqlDbType.Uuid, userId);
                TenantDb.Add(cmd, "inbox", NpgsqlDbType.Uuid, inboxId);
                TenantDb.Add(cmd, "channel", NpgsqlDbType.Varchar, item.Channel);
                TenantDb.Add(cmd, "messageId", NpgsqlDbType.Varchar, item.MessageId is { Length: > 500 } m ? m[..500] : item.MessageId);
                TenantDb.Add(cmd, "sender", NpgsqlDbType.Varchar, Truncate(item.Sender, 320));
                TenantDb.Add(cmd, "senderName", NpgsqlDbType.Varchar, item.SenderName is null ? null : Truncate(item.SenderName, 300));
                TenantDb.Add(cmd, "subject", NpgsqlDbType.Varchar, Truncate(item.Subject, 500));
                TenantDb.Add(cmd, "body", NpgsqlDbType.Text, item.BodyText is { Length: > 20_000 } b ? b[..20_000] : item.BodyText);
                TenantDb.Add(cmd, "receivedAt", NpgsqlDbType.TimestampTz, item.ReceivedAt);
                TenantDb.Add(cmd, "status", NpgsqlDbType.Varchar, status);
                TenantDb.Add(cmd, "fileIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid, fileIds.ToArray());
                TenantDb.Add(cmd, "duplicateIds", NpgsqlDbType.Array | NpgsqlDbType.Uuid, duplicates.ToArray());
                TenantDb.Add(cmd, "reject", NpgsqlDbType.Text, rejectReason);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            if (status == "new")
            {
                await PublishReceivedAsync(connection, tx, itemId, inbox, item, fileIds, duplicates, correlationId, cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
        }
        catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation && item.MessageId is not null)
        {
            // Two deliveries of the same message raced; the first one wins.
            await tx.RollbackAsync(cancellationToken);
            return await FindByMessageIdAsync(connection, inboxId, item.MessageId, cancellationToken)
                ?? throw new InvalidOperationException("Inbox item disappeared.");
        }

        eventPublisher.NotifyCommitted();
        return (await GetItemAsync(itemId, cancellationToken))!;
    }

    /// <summary>
    /// Called after a file that was waiting for a scan has been scanned: items whose files are all resolved leave
    /// <c>scanning</c> (rejected if anything was infected, otherwise new and announced).
    /// </summary>
    public async Task ReleaseScannedItemsAsync(Guid fileId, CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);

        var waiting = new List<Guid>();
        await using (var cmd = TenantDb.Create(
            connection, null, "SELECT id FROM core.inboxitem WHERE status = 'scanning' AND @file = ANY (file_ids);"))
        {
            TenantDb.Add(cmd, "file", NpgsqlDbType.Uuid, fileId);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                waiting.Add(reader.GetGuid(0));
            }
        }

        foreach (var itemId in waiting)
        {
            var item = (await GetItemAsync(itemId, cancellationToken))!;
            var itemFiles = await files.GetManyAsync(item.FileIds, cancellationToken);
            var infected = itemFiles.Where(f => f.ScanStatus == "infected").ToList();
            if (infected.Count == 0 && itemFiles.Any(f => f.ScanStatus == "pending"))
            {
                continue;
            }

            var inbox = (await GetInboxAsync(item.InboxId, cancellationToken))!;
            var correlationId = Guid.NewGuid().ToString("D");
            await using var tx = await connection.BeginTransactionAsync(cancellationToken);

            await using (var update = TenantDb.Create(
                connection,
                tx,
                """
                UPDATE core.inboxitem
                   SET status = @status, reject_reason = @reason, updated_at = now(), row_version = row_version + 1
                 WHERE id = @id AND status = 'scanning';
                """))
            {
                TenantDb.Add(update, "id", NpgsqlDbType.Uuid, itemId);
                TenantDb.Add(update, "status", NpgsqlDbType.Varchar, infected.Count > 0 ? "rejected" : "new");
                TenantDb.Add(
                    update, "reason", NpgsqlDbType.Text,
                    infected.Count > 0
                        ? $"Virus found in attachment: {string.Join(", ", infected.Select(f => $"{f.FileName} ({f.ScanSignature})"))}."
                        : null);
                if (await update.ExecuteNonQueryAsync(cancellationToken) == 0)
                {
                    await tx.RollbackAsync(cancellationToken);
                    continue;
                }
            }

            if (infected.Count == 0)
            {
                await PublishReceivedAsync(
                    connection, tx, itemId, inbox,
                    new IngestItem(item.Channel, item.MessageId, item.Sender, item.SenderName, item.Subject, item.BodyText, item.ReceivedAt, []),
                    item.FileIds, item.DuplicateFileIds, correlationId, cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
        }

        eventPublisher.NotifyCommitted();
    }

    // ------------------------------------------------------------------ worklist

    public async Task<IReadOnlyList<InboxItemInfo>> ListItemsAsync(
        string? status,
        Guid? inboxId,
        int top,
        CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(
            connection,
            null,
            $"""
            SELECT {ItemColumns} FROM core.inboxitem
             WHERE (@status::text IS NULL OR status = @status) AND (@inbox::uuid IS NULL OR inbox_id = @inbox)
             ORDER BY received_at DESC, id
             LIMIT @top;
            """);
        TenantDb.Add(cmd, "status", NpgsqlDbType.Text, string.IsNullOrWhiteSpace(status) ? null : status);
        TenantDb.Add(cmd, "inbox", NpgsqlDbType.Uuid, inboxId);
        TenantDb.Add(cmd, "top", NpgsqlDbType.Integer, Math.Clamp(top, 1, 200));
        var result = new List<InboxItemInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(ReadItem(reader));
        }

        return result;
    }

    public async Task<int> CountNewAsync(CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(connection, null, "SELECT count(*) FROM core.inboxitem WHERE status = 'new';");
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<InboxItemInfo?> GetItemAsync(Guid itemId, CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        return await GetItemAsync(connection, null, itemId, cancellationToken);
    }

    /// <summary>Takes an unclaimed item. Atomic: exactly one claimant wins; others get a conflict.</summary>
    public async Task<InboxItemInfo> ClaimAsync(Guid itemId, string claimedBy, CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(
            connection,
            null,
            """
            UPDATE core.inboxitem
               SET status = 'claimed', claimed_by = @by, claimed_at = now(), updated_at = now(), row_version = row_version + 1
             WHERE id = @id AND status = 'new'
            RETURNING id;
            """);
        TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, itemId);
        TenantDb.Add(cmd, "by", NpgsqlDbType.Varchar, claimedBy);
        if (await cmd.ExecuteScalarAsync(cancellationToken) is null)
        {
            var current = await GetItemAsync(itemId, cancellationToken)
                ?? throw new KeyNotFoundException($"Inbox item '{itemId}' was not found.");
            throw new InboxItemStateException($"Inbox item is {current.Status} and cannot be claimed.");
        }

        InvalidateCounts();
        return (await GetItemAsync(itemId, cancellationToken))!;
    }

    /// <summary>Gives a claimed item back to the worklist.</summary>
    public Task<InboxItemInfo> ReleaseAsync(Guid itemId, string claimedBy, CancellationToken cancellationToken) =>
        TransitionAsync(
            itemId,
            claimedBy,
            "status = 'new', claimed_by = NULL, claimed_at = NULL",
            requireClaimant: true,
            extra: null,
            cancellationToken);

    /// <summary>Marks a claimed item processed and records the object it resulted in.</summary>
    public Task<InboxItemInfo> CompleteAsync(
        Guid itemId,
        string claimedBy,
        string? resultObjectId,
        Guid? resultRecordId,
        CancellationToken cancellationToken) =>
        TransitionAsync(
            itemId,
            claimedBy,
            "status = 'processed', processed_at = now(), result_object_id = @resultObject, result_record_id = @resultRecord",
            requireClaimant: true,
            extra: cmd =>
            {
                TenantDb.Add(cmd, "resultObject", NpgsqlDbType.Varchar, resultObjectId);
                TenantDb.Add(cmd, "resultRecord", NpgsqlDbType.Uuid, resultRecordId);
            },
            cancellationToken);

    /// <summary>Rejects an item that is new or claimed by the caller.</summary>
    public Task<InboxItemInfo> RejectAsync(Guid itemId, string claimedBy, string? reason, CancellationToken cancellationToken) =>
        TransitionAsync(
            itemId,
            claimedBy,
            "status = 'rejected', reject_reason = @reason, processed_at = now()",
            requireClaimant: false,
            extra: cmd => TenantDb.Add(cmd, "reason", NpgsqlDbType.Text, string.IsNullOrWhiteSpace(reason) ? "Rejected." : reason.Trim()),
            cancellationToken);

    /// <summary>
    /// Manual processing from the worklist: attaches every file of the item to a record, then marks it processed.
    /// The caller takes the item (claim) if it is still new.
    /// </summary>
    public async Task<InboxItemInfo> AttachToRecordAsync(
        Guid itemId,
        string claimedBy,
        string objectId,
        Guid recordId,
        string category,
        CancellationToken cancellationToken)
    {
        var item = await GetItemAsync(itemId, cancellationToken)
            ?? throw new KeyNotFoundException($"Inbox item '{itemId}' was not found.");
        if (item.Status == "new")
        {
            item = await ClaimAsync(itemId, claimedBy, cancellationToken);
        }

        if (item.Status != "claimed" || item.ClaimedBy != claimedBy)
        {
            throw new InboxItemStateException($"Inbox item is {item.Status} and cannot be processed by this caller.");
        }

        foreach (var fileId in item.FileIds)
        {
            await files.AttachAsync(fileId, objectId, recordId, category, cancellationToken);
        }

        return await CompleteAsync(itemId, claimedBy, objectId, recordId, cancellationToken);
    }

    // ------------------------------------------------------------------ internals

    private async Task<InboxItemInfo> TransitionAsync(
        Guid itemId,
        string claimedBy,
        string setClause,
        bool requireClaimant,
        Action<DbCommand>? extra,
        CancellationToken cancellationToken)
    {
        var connection = await TenantDb.OpenAsync(dbContext, tenantContext, cancellationToken);
        await using var cmd = TenantDb.Create(
            connection,
            null,
            $"""
            UPDATE core.inboxitem
               SET {setClause}, updated_at = now(), row_version = row_version + 1
             WHERE id = @id
               AND (status = 'claimed' AND claimed_by = @by{(requireClaimant ? string.Empty : " OR status = 'new'")})
            RETURNING id;
            """);
        TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, itemId);
        TenantDb.Add(cmd, "by", NpgsqlDbType.Varchar, claimedBy);
        extra?.Invoke(cmd);
        if (await cmd.ExecuteScalarAsync(cancellationToken) is null)
        {
            var current = await GetItemAsync(itemId, cancellationToken)
                ?? throw new KeyNotFoundException($"Inbox item '{itemId}' was not found.");
            throw new InboxItemStateException(
                $"Inbox item is {current.Status}{(current.ClaimedBy is null ? string.Empty : $" (claimed by {current.ClaimedBy})")} and the action is not allowed.");
        }

        InvalidateCounts();
        return (await GetItemAsync(itemId, cancellationToken))!;
    }

    /// <summary>The start page counts new items; claiming or processing one changes that count.</summary>
    private void InvalidateCounts()
    {
        if (tenantContext.TenantId is { } tenantId)
        {
            attentionCache.InvalidateTenant(tenantId);
        }
    }

    private async Task PublishReceivedAsync(
        DbConnection connection,
        DbTransaction tx,
        Guid itemId,
        InboxInfo inbox,
        IngestItem item,
        IReadOnlyList<Guid> fileIds,
        IReadOnlyList<Guid> duplicateFileIds,
        string correlationId,
        CancellationToken cancellationToken) =>
        await eventPublisher.PublishAsync(
            connection,
            tx,
            "core.inbox.received@1",
            itemId.ToString("D"),
            new
            {
                itemId,
                inboxId = inbox.Id,
                companyId = inbox.CompanyId,
                channel = item.Channel,
                from = item.Sender,
                subject = item.Subject,
                fileIds,
                duplicateFileIds
            },
            inbox.CompanyId,
            correlationId,
            cancellationToken);

    private static async Task<InboxItemInfo?> FindByMessageIdAsync(
        DbConnection connection,
        Guid inboxId,
        string messageId,
        CancellationToken cancellationToken)
    {
        await using var cmd = TenantDb.Create(
            connection, null, $"SELECT {ItemColumns} FROM core.inboxitem WHERE inbox_id = @inbox AND message_id = @message;");
        TenantDb.Add(cmd, "inbox", NpgsqlDbType.Uuid, inboxId);
        TenantDb.Add(cmd, "message", NpgsqlDbType.Varchar, messageId.Length > 500 ? messageId[..500] : messageId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadItem(reader) : null;
    }

    private static async Task<InboxItemInfo?> GetItemAsync(
        DbConnection connection,
        DbTransaction? tx,
        Guid itemId,
        CancellationToken cancellationToken)
    {
        await using var cmd = TenantDb.Create(connection, tx, $"SELECT {ItemColumns} FROM core.inboxitem WHERE id = @id;");
        TenantDb.Add(cmd, "id", NpgsqlDbType.Uuid, itemId);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadItem(reader) : null;
    }

    private static InboxInfo ReadInbox(DbDataReader r) => new(
        r.GetGuid(0),
        r.IsDBNull(1) ? null : r.GetGuid(1),
        r.GetString(2),
        r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.GetBoolean(5),
        r.GetFieldValue<DateTimeOffset>(6));

    private static InboxItemInfo ReadItem(DbDataReader r) => new(
        r.GetGuid(0),
        r.GetGuid(1),
        r.IsDBNull(2) ? null : r.GetGuid(2),
        r.GetString(3),
        r.IsDBNull(4) ? null : r.GetString(4),
        r.GetString(5),
        r.IsDBNull(6) ? null : r.GetString(6),
        r.GetString(7),
        r.IsDBNull(8) ? null : r.GetString(8),
        r.GetFieldValue<DateTimeOffset>(9),
        r.GetString(10),
        r.GetFieldValue<Guid[]>(11),
        r.GetFieldValue<Guid[]>(12),
        r.IsDBNull(13) ? null : r.GetString(13),
        r.IsDBNull(14) ? null : r.GetFieldValue<DateTimeOffset>(14),
        r.IsDBNull(15) ? null : r.GetFieldValue<DateTimeOffset>(15),
        r.IsDBNull(16) ? null : r.GetString(16),
        r.IsDBNull(17) ? null : r.GetGuid(17),
        r.IsDBNull(18) ? null : r.GetString(18));

    private static string Truncate(string value, int max) => value.Length > max ? value[..max] : value;
}

/// <summary>The requested action is not possible in the inbox item's current state (e.g. already claimed).</summary>
public sealed class InboxItemStateException(string message) : Exception(message);
