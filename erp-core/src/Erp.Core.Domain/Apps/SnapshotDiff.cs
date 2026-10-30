namespace Erp.Core.Domain.Apps;

/// <summary>Compares two metadata snapshots of a tenant: what was added, removed or changed between them.</summary>
public static class SnapshotDiff
{
    public static IReadOnlyList<SchemaChange> Compute(TenantMetadataSnapshotPayload from, TenantMetadataSnapshotPayload to)
    {
        var changes = new List<SchemaChange>();

        var oldApps = from.InstalledApps.ToDictionary(a => a.AppId, StringComparer.OrdinalIgnoreCase);
        var newApps = to.InstalledApps.ToDictionary(a => a.AppId, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, app) in newApps)
        {
            if (!oldApps.TryGetValue(id, out var before))
            {
                changes.Add(new("app", id, $"App '{id}' {app.Version} was installed.", Breaking: false));
            }
            else if (!string.Equals(before.Version, app.Version, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new("app", id, $"App '{id}' changed from {before.Version} to {app.Version}.", Breaking: false));
            }
        }

        changes.AddRange(oldApps.Keys.Where(id => !newApps.ContainsKey(id))
            .Select(id => new SchemaChange("app", id, $"App '{id}' was uninstalled.", Breaking: true)));

        var oldObjects = from.Objects.ToDictionary(o => o.Id, StringComparer.OrdinalIgnoreCase);
        var newObjects = to.Objects.ToDictionary(o => o.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, obj) in newObjects)
        {
            if (!oldObjects.TryGetValue(id, out var before))
            {
                changes.Add(new("object", id, $"Object '{id}' was added.", Breaking: false));
                continue;
            }

            var oldFields = before.Fields.ToDictionary(f => $"{f.OwnerApp}:{f.Name}", StringComparer.OrdinalIgnoreCase);
            var newFields = obj.Fields.ToDictionary(f => $"{f.OwnerApp}:{f.Name}", StringComparer.OrdinalIgnoreCase);
            foreach (var (key, field) in newFields)
            {
                if (!oldFields.TryGetValue(key, out var oldField))
                {
                    changes.Add(new("field", $"{id}.{field.Name}", $"Field '{field.Name}' ({field.OwnerApp}) was added to '{id}'.", Breaking: false));
                }
                else if (oldField.Type != field.Type)
                {
                    changes.Add(new("field", $"{id}.{field.Name}", $"Field '{field.Name}' on '{id}' changed type from {oldField.Type} to {field.Type}.", Breaking: true));
                }
            }

            changes.AddRange(oldFields.Where(kv => !newFields.ContainsKey(kv.Key))
                .Select(kv => new SchemaChange("field", $"{id}.{kv.Value.Name}", $"Field '{kv.Value.Name}' ({kv.Value.OwnerApp}) was removed from '{id}'.", Breaking: true)));
        }

        changes.AddRange(oldObjects.Keys.Where(id => !newObjects.ContainsKey(id))
            .Select(id => new SchemaChange("object", id, $"Object '{id}' was removed.", Breaking: true)));

        var oldEnums = from.Enums.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        var newEnums = to.Enums.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, enumDef) in newEnums)
        {
            if (!oldEnums.TryGetValue(id, out var before))
            {
                changes.Add(new("enum", id, $"Enum '{id}' was added.", Breaking: false));
                continue;
            }

            changes.AddRange(enumDef.Values.Except(before.Values, StringComparer.OrdinalIgnoreCase)
                .Select(v => new SchemaChange("enum", $"{id}.{v}", $"Enum value '{v}' was added to '{id}'.", Breaking: false)));
            changes.AddRange(before.Values.Except(enumDef.Values, StringComparer.OrdinalIgnoreCase)
                .Select(v => new SchemaChange("enum", $"{id}.{v}", $"Enum value '{v}' was removed from '{id}'.", Breaking: true)));
        }

        changes.AddRange(oldEnums.Keys.Where(id => !newEnums.ContainsKey(id))
            .Select(id => new SchemaChange("enum", id, $"Enum '{id}' was removed.", Breaking: true)));

        var oldEvents = from.Events.Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newEvents = to.Events.Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        changes.AddRange(newEvents.Except(oldEvents, StringComparer.OrdinalIgnoreCase)
            .Select(id => new SchemaChange("event", id, $"Event '{id}' was added.", Breaking: false)));
        changes.AddRange(oldEvents.Except(newEvents, StringComparer.OrdinalIgnoreCase)
            .Select(id => new SchemaChange("event", id, $"Event '{id}' was removed.", Breaking: true)));

        return changes;
    }
}
