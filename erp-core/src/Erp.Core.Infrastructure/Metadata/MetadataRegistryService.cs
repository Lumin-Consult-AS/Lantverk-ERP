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

            var existing = await GetObjectAsync(objectDef.Id, cancellationToken);
            if (existing is not null)
            {
                if (!string.Equals(existing.OwnerApp, objectDef.OwnerApp, StringComparison.OrdinalIgnoreCase))
                {
                    throw new MetadataValidationException(
                        $"Object '{objectDef.Id}' is owned by '{existing.OwnerApp}' and cannot be redefined by '{objectDef.OwnerApp}'.");
                }

                // Preserve any extension fields owned by other apps
                var extensionFields = existing.Fields
                    .Where(f => !string.Equals(f.OwnerApp, objectDef.OwnerApp, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                foreach (var extField in extensionFields)
                {
                    if (objectDef.Fields.All(f => !string.Equals(f.GetColumnName(objectDef.OwnerApp), extField.GetColumnName(objectDef.OwnerApp), StringComparison.OrdinalIgnoreCase)))
                    {
                        objectDef.Fields.Add(extField);
                    }
                }
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
                extField.OwnerApp = string.IsNullOrWhiteSpace(extField.OwnerApp) ? ext.ExtendingApp : extField.OwnerApp;

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

