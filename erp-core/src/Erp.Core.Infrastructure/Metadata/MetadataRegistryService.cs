using System.Text.Json;
using System.Text.Json.Serialization;
using Erp.Core.Domain.Data;
using Erp.Core.Domain.Entities;
using Erp.Core.Domain.Metadata;
using Erp.Core.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Erp.Core.Infrastructure.Metadata;

/// <summary>
/// Manages registration of metadata definitions (objects, extensions, enums, interfaces, events)
/// per tenant and executes lock-safe DDL to provision schemas, tables, RLS policies, and audit triggers.
/// </summary>
public sealed class MetadataRegistryService(
    CoreDbContext dbContext,
    IConfiguration configuration)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private string MigrationConnectionString =>
        configuration.GetConnectionString("MigrationConnection")
        ?? configuration.GetConnectionString("DefaultConnection")
        ?? "Host=localhost;Port=5432;Database=erp;Username=erp_migrator;Password=erp_migrator_dev";

    public async Task<IReadOnlyList<ObjectDefinition>> ApplyYamlAsync(
        string yamlContent,
        CancellationToken cancellationToken = default)
    {
        var bundle = YamlMetadataParser.Parse(yamlContent);
        return await ApplyBundleAsync(bundle, cancellationToken);
    }

    public async Task<IReadOnlyList<ObjectDefinition>> ApplyBundleAsync(
        MetadataBundle bundle,
        CancellationToken cancellationToken = default)
    {
        var affectedObjects = new List<ObjectDefinition>();

        foreach (var enumDef in bundle.Enums)
        {
            await UpsertMetadataItemAsync("enum", enumDef.Id, enumDef.OwnerApp, bundle.Version, enumDef, cancellationToken);
        }

        foreach (var ifaceDef in bundle.Interfaces)
        {
            await UpsertMetadataItemAsync("interface", ifaceDef.Id, ifaceDef.OwnerApp, bundle.Version, ifaceDef, cancellationToken);
        }

        foreach (var eventDef in bundle.Events)
        {
            await UpsertMetadataItemAsync("event", eventDef.Id, eventDef.OwnerApp, bundle.Version, eventDef, cancellationToken);
        }

        foreach (var objectDef in bundle.Objects)
        {
            ValidateObjectDefinition(objectDef);

            foreach (var f in objectDef.Fields)
            {
                if (!string.IsNullOrWhiteSpace(f.OwnerApp)
                    && !string.Equals(f.OwnerApp, objectDef.OwnerApp, StringComparison.OrdinalIgnoreCase))
                {
                    throw new MetadataValidationException(
                        $"Object owner '{objectDef.OwnerApp}' cannot define or modify field '{f.Name}' owned by '{f.OwnerApp}' on '{objectDef.Id}'.");
                }

                if (f.Name.Contains('.', StringComparison.Ordinal))
                {
                    throw new MetadataValidationException(
                        $"Object owner '{objectDef.OwnerApp}' cannot define dotted extension field name '{f.Name}' on '{objectDef.Id}'.");
                }
            }

            var existing = await GetObjectAsync(objectDef.Id, cancellationToken);
            if (existing is not null)
            {
                if (!string.Equals(existing.OwnerApp, objectDef.OwnerApp, StringComparison.OrdinalIgnoreCase))
                {
                    throw new MetadataValidationException(
                        $"Object '{objectDef.Id}' is owned by '{existing.OwnerApp}' and cannot be redefined by '{objectDef.OwnerApp}'.");
                }

                // Preserve any extension fields owned by other apps and orphaned columns
                var extensionFields = existing.Fields
                    .Where(f => !string.Equals(f.OwnerApp, objectDef.OwnerApp, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var extField in extensionFields)
                {
                    var extCol = extField.GetColumnName(objectDef.OwnerApp);
                    if (objectDef.Fields.Any(f => string.Equals(f.GetColumnName(objectDef.OwnerApp), extCol, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new MetadataValidationException(
                            $"Object owner '{objectDef.OwnerApp}' cannot overwrite extension field '{extField.Name}' owned by '{extField.OwnerApp}'.");
                    }

                    objectDef.Fields.Add(extField);
                }

                objectDef.OrphanedColumns = existing.OrphanedColumns;
            }

            await UpsertMetadataItemAsync("object", objectDef.Id, objectDef.OwnerApp, objectDef.Version, objectDef, cancellationToken);
            affectedObjects.Add(objectDef);
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        foreach (var ext in bundle.Extensions)
        {
            var targetObj = await GetObjectAsync(ext.TargetObject, cancellationToken)
                ?? throw new MetadataValidationException($"Cannot extend unknown object '{ext.TargetObject}'.");

            if (targetObj.IsSealed)
            {
                throw new MetadataValidationException($"Object '{ext.TargetObject}' is sealed and cannot be extended.");
            }

            foreach (var extField in ext.Fields)
            {
                if (!string.IsNullOrWhiteSpace(extField.OwnerApp)
                    && !string.Equals(extField.OwnerApp, ext.ExtendingApp, StringComparison.OrdinalIgnoreCase))
                {
                    throw new MetadataValidationException(
                        $"Extending app '{ext.ExtendingApp}' cannot modify field '{extField.Name}' owned by '{extField.OwnerApp}' on '{targetObj.Id}'.");
                }

                if (extField.Name.Contains('.', StringComparison.Ordinal))
                {
                    throw new MetadataValidationException(
                        $"Extension field '{extField.Name}' on '{targetObj.Id}' cannot use a dotted prefix; it is implicitly prefixed with '{ext.ExtendingApp}'.");
                }

                extField.OwnerApp = ext.ExtendingApp;

                var existingOwnerField = targetObj.Fields.FirstOrDefault(f =>
                    string.Equals(f.Name, extField.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(f.OwnerApp, targetObj.OwnerApp, StringComparison.OrdinalIgnoreCase));

                if (existingOwnerField is not null && string.Equals(extField.OwnerApp, targetObj.OwnerApp, StringComparison.OrdinalIgnoreCase))
                {
                    throw new MetadataValidationException($"Extension cannot overwrite owner field '{extField.Name}' on '{targetObj.Id}'.");
                }

                targetObj.Fields.RemoveAll(f =>
                    string.Equals(f.Name, extField.Name, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(f.OwnerApp, extField.OwnerApp, StringComparison.OrdinalIgnoreCase));

                var colName = extField.GetColumnName(targetObj.OwnerApp);
                targetObj.OrphanedColumns.RemoveAll(o =>
                    string.Equals(o.ColumnName, colName, StringComparison.OrdinalIgnoreCase));

                targetObj.Fields.Add(extField);
            }

            await UpsertMetadataItemAsync("object", targetObj.Id, targetObj.OwnerApp, targetObj.Version, targetObj, cancellationToken);
            var existingIdx = affectedObjects.FindIndex(o => string.Equals(o.Id, targetObj.Id, StringComparison.OrdinalIgnoreCase));
            if (existingIdx >= 0)
            {
                affectedObjects[existingIdx] = targetObj;
            }
            else
            {
                affectedObjects.Add(targetObj);
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        foreach (var obj in affectedObjects)
        {
            await ProvisionObjectSchemaAsync(obj, cancellationToken);
        }

        return affectedObjects;
    }

    /// <summary>
    /// Drops from the tenant's metadata what <paramref name="appId"/> owned but no longer declares in
    /// <paramref name="current"/> (the version being upgraded to): objects, enums, events and interfaces, and fields it owns on
    /// objects. Removed fields become orphaned columns (kept until <paramref name="dropAfter"/>); the physical tables and
    /// columns are never touched here. Returns the ids of the removed objects, whose data the caller archives.
    /// </summary>
    public async Task<IReadOnlyList<string>> ReconcileOwnedAsync(
        string appId,
        MetadataBundle current,
        DateTimeOffset dropAfter,
        CancellationToken cancellationToken = default)
    {
        var keepObjects = current.Objects.Select(o => o.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keepEnums = current.Enums.Select(o => o.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keepEvents = current.Events.Select(o => o.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var keepInterfaces = current.Interfaces.Select(o => o.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var removedObjects = new List<string>();
        var owned = await dbContext.MetadataItems.Where(m => m.OwnerApp == appId).ToListAsync(cancellationToken);
        foreach (var item in owned)
        {
            var kept = item.Kind switch
            {
                "object" => keepObjects.Contains(item.ItemId),
                "enum" => keepEnums.Contains(item.ItemId),
                "event" => keepEvents.Contains(item.ItemId),
                "interface" => keepInterfaces.Contains(item.ItemId),
                _ => true
            };

            if (!kept)
            {
                dbContext.MetadataItems.Remove(item);
                if (item.Kind == "object")
                {
                    removedObjects.Add(item.ItemId);
                }
            }
        }

        // Fields the app owns, on its own objects and as extensions of other apps' objects
        var objectItems = await dbContext.MetadataItems.Where(m => m.Kind == "object").ToListAsync(cancellationToken);
        foreach (var item in objectItems.Where(i => !removedObjects.Contains(i.ItemId, StringComparer.OrdinalIgnoreCase)))
        {
            var definition = JsonSerializer.Deserialize<ObjectDefinition>(item.DefinitionJson, JsonOptions);
            if (definition is null)
            {
                continue;
            }

            var currentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var own = current.Objects.FirstOrDefault(o => string.Equals(o.Id, definition.Id, StringComparison.OrdinalIgnoreCase));
            if (own is not null)
            {
                currentNames.UnionWith(own.Fields.Select(f => f.Name));
            }

            foreach (var ext in current.Extensions.Where(e => string.Equals(e.TargetObject, definition.Id, StringComparison.OrdinalIgnoreCase)))
            {
                currentNames.UnionWith(ext.Fields.Select(f => f.Name));
            }

            var removed = definition.Fields
                .Where(f => string.Equals(f.OwnerApp, appId, StringComparison.OrdinalIgnoreCase) && !currentNames.Contains(f.Name))
                .ToList();
            if (removed.Count == 0)
            {
                continue;
            }

            foreach (var field in removed)
            {
                var column = field.GetColumnName(definition.OwnerApp);
                definition.OrphanedColumns.RemoveAll(o => string.Equals(o.ColumnName, column, StringComparison.OrdinalIgnoreCase));
                definition.OrphanedColumns.Add(new OrphanedColumnDefinition
                {
                    ColumnName = column,
                    FieldName = field.Name,
                    FormerOwnerApp = appId,
                    Type = field.Type,
                    OrphanedAt = DateTimeOffset.UtcNow,
                    DropAfter = dropAfter
                });
            }

            definition.Fields.RemoveAll(f => removed.Contains(f));
            item.DefinitionJson = JsonSerializer.Serialize(definition, JsonOptions);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return removedObjects;
    }

    public async Task<ObjectDefinition?> GetObjectAsync(string objectId, CancellationToken cancellationToken = default)
    {
        var item = await dbContext.MetadataItems
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Kind == "object" && m.ItemId == objectId, cancellationToken);

        return item is null
            ? null
            : JsonSerializer.Deserialize<ObjectDefinition>(item.DefinitionJson, JsonOptions);
    }

    public async Task<IReadOnlyList<ObjectDefinition>> GetObjectsAsync(CancellationToken cancellationToken = default)
    {
        var items = await dbContext.MetadataItems
            .AsNoTracking()
            .Where(m => m.Kind == "object")
            .OrderBy(m => m.ItemId)
            .ToListAsync(cancellationToken);

        return items
            .Select(i => JsonSerializer.Deserialize<ObjectDefinition>(i.DefinitionJson, JsonOptions)!)
            .ToList();
    }

    public async Task<EnumDefinition?> GetEnumAsync(string enumId, CancellationToken cancellationToken = default)
    {
        var item = await dbContext.MetadataItems
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Kind == "enum" && m.ItemId == enumId, cancellationToken);

        return item is null
            ? null
            : JsonSerializer.Deserialize<EnumDefinition>(item.DefinitionJson, JsonOptions);
    }

    public async Task<IReadOnlyList<EnumDefinition>> GetEnumsAsync(CancellationToken cancellationToken = default)
    {
        var items = await dbContext.MetadataItems
            .AsNoTracking()
            .Where(m => m.Kind == "enum")
            .OrderBy(m => m.ItemId)
            .ToListAsync(cancellationToken);

        return items
            .Select(i => JsonSerializer.Deserialize<EnumDefinition>(i.DefinitionJson, JsonOptions)!)
            .ToList();
    }

    private async Task UpsertMetadataItemAsync<T>(
        string kind,
        string itemId,
        string ownerApp,
        string version,
        T payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var existing = await dbContext.MetadataItems
            .FirstOrDefaultAsync(m => m.Kind == kind && m.ItemId == itemId, cancellationToken);

        if (existing is null)
        {
            dbContext.MetadataItems.Add(new MetadataItem
            {
                Id = Guid.NewGuid(),
                Kind = kind,
                ItemId = itemId,
                OwnerApp = ownerApp,
                Version = version,
                DefinitionJson = json
            });
        }
        else
        {
            existing.Version = version;
            existing.DefinitionJson = json;
        }
    }

    private async Task ProvisionObjectSchemaAsync(ObjectDefinition objectDef, CancellationToken cancellationToken)
    {
        var plan = MetadataDdlGenerator.GenerateForObject(objectDef);

        await using var conn = new NpgsqlConnection(MigrationConnectionString);
        await conn.OpenAsync(cancellationToken);

        await using (var tx = await conn.BeginTransactionAsync(cancellationToken))
        {
            foreach (var sql in plan.TransactionalStatements)
            {
                await using var cmd = new NpgsqlCommand(sql, conn, tx);
                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }

            await tx.CommitAsync(cancellationToken);
        }

        // Concurrent indexes cannot run inside a transaction block
        foreach (var indexSql in plan.ConcurrentIndexStatements)
        {
            await using var cmd = new NpgsqlCommand(indexSql, conn);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static void ValidateObjectDefinition(ObjectDefinition objectDef)
    {
        _ = objectDef.GetSchemaAndTable();

        foreach (var field in objectDef.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name))
            {
                throw new MetadataValidationException($"Object '{objectDef.Id}' contains a field with an empty name.");
            }

            _ = field.GetColumnName(objectDef.OwnerApp);

            if (field.Type == FieldType.Reference && string.IsNullOrWhiteSpace(field.TargetObject))
            {
                throw new MetadataValidationException($"Reference field '{field.Name}' on '{objectDef.Id}' must specify target object ('to').");
            }

            if (field.Type == FieldType.Enum && string.IsNullOrWhiteSpace(field.EnumType))
            {
                throw new MetadataValidationException($"Enum field '{field.Name}' on '{objectDef.Id}' must specify enum type ('of').");
            }
        }
    }
}

