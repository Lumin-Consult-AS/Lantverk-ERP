using Erp.Core.Domain.Metadata;

namespace Erp.Core.Domain.Apps;

/// <summary>One difference between two versions of an app's schema.</summary>
public sealed record SchemaChange(string Kind, string Subject, string Message, bool Breaking);

/// <summary>An upgrade was refused; <see cref="Errors"/> says why.</summary>
public sealed class AppUpgradeException(IReadOnlyList<string> errors)
    : Exception("The upgrade is not allowed: " + string.Join(" ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

/// <summary>
/// Compares two versions of an app and classifies the differences (ADR-0013). New fields, enum values, objects and events
/// are additive (minor). Removing or retyping something other code may depend on is breaking (major).
/// </summary>
public static class AppUpgradeAnalyzer
{
    public static IReadOnlyList<SchemaChange> Analyze(AppManifest installed, AppManifest next)
    {
        var changes = new List<SchemaChange>();

        var oldObjects = installed.Metadata.Objects.ToDictionary(o => o.Id, StringComparer.OrdinalIgnoreCase);
        var newObjects = next.Metadata.Objects.ToDictionary(o => o.Id, StringComparer.OrdinalIgnoreCase);

        foreach (var (id, oldObject) in oldObjects)
        {
            if (!newObjects.TryGetValue(id, out var newObject))
            {
                changes.Add(new("object", id, $"Object '{id}' was removed.", Breaking: true));
                continue;
            }

            CompareFields(id, oldObject.Fields, newObject.Fields, changes);
        }

        foreach (var id in newObjects.Keys.Where(id => !oldObjects.ContainsKey(id)))
        {
            changes.Add(new("object", id, $"Object '{id}' was added.", Breaking: false));
        }

        // Fields this app adds to other apps' objects
        var oldExtensions = installed.Metadata.Extensions.ToDictionary(e => e.TargetObject, StringComparer.OrdinalIgnoreCase);
        var newExtensions = next.Metadata.Extensions.ToDictionary(e => e.TargetObject, StringComparer.OrdinalIgnoreCase);
        foreach (var (target, oldExt) in oldExtensions)
        {
            var newFields = newExtensions.TryGetValue(target, out var newExt) ? newExt.Fields : [];
            CompareFields($"{target} (extension)", oldExt.Fields, newFields, changes);
        }

        foreach (var (target, newExt) in newExtensions.Where(kv => !oldExtensions.ContainsKey(kv.Key)))
        {
            changes.AddRange(newExt.Fields.Select(f => new SchemaChange(
                "field", $"{target}.{f.Name}", $"Extension field '{f.Name}' on '{target}' was added.", Breaking: false)));
        }

        var oldEnums = installed.Metadata.Enums.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        var newEnums = next.Metadata.Enums.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var (id, oldEnum) in oldEnums)
        {
            if (!newEnums.TryGetValue(id, out var newEnum))
            {
                changes.Add(new("enum", id, $"Enum '{id}' was removed.", Breaking: true));
                continue;
            }

            foreach (var removed in oldEnum.Values.Except(newEnum.Values, StringComparer.OrdinalIgnoreCase))
            {
                changes.Add(new("enum", $"{id}.{removed}", $"Enum value '{removed}' was removed from '{id}'.", Breaking: true));
            }

            foreach (var added in newEnum.Values.Except(oldEnum.Values, StringComparer.OrdinalIgnoreCase))
            {
                changes.Add(new("enum", $"{id}.{added}", $"Enum value '{added}' was added to '{id}'.", Breaking: false));
            }
        }

        var oldEvents = installed.Metadata.Events.Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newEvents = next.Metadata.Events.Select(e => e.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        changes.AddRange(oldEvents.Except(newEvents, StringComparer.OrdinalIgnoreCase)
            .Select(id => new SchemaChange("event", id, $"Published event '{id}' was removed.", Breaking: true)));
        changes.AddRange(newEvents.Except(oldEvents, StringComparer.OrdinalIgnoreCase)
            .Select(id => new SchemaChange("event", id, $"Event '{id}' was added.", Breaking: false)));

        return changes;
    }

    private static void CompareFields(
        string owner,
        IEnumerable<FieldDefinition> oldFields,
        IEnumerable<FieldDefinition> newFields,
        List<SchemaChange> changes)
    {
        var next = newFields.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);
        var previous = oldFields.ToDictionary(f => f.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var (name, oldField) in previous)
        {
            var subject = $"{owner}.{name}";
            if (!next.TryGetValue(name, out var newField))
            {
                changes.Add(new("field", subject, $"Field '{name}' on '{owner}' was removed.", Breaking: true));
                continue;
            }

            if (oldField.Type != newField.Type)
            {
                changes.Add(new("field", subject, $"Field '{name}' on '{owner}' changed type from {oldField.Type} to {newField.Type}.", Breaking: true));
            }
            else if (!string.Equals(oldField.EnumType, newField.EnumType, StringComparison.OrdinalIgnoreCase)
                     || !string.Equals(oldField.TargetObject, newField.TargetObject, StringComparison.OrdinalIgnoreCase))
            {
                changes.Add(new("field", subject, $"Field '{name}' on '{owner}' now refers to a different enum or object.", Breaking: true));
            }
            else if (newField.MaxLength < oldField.MaxLength || newField.Scale < oldField.Scale)
            {
                changes.Add(new("field", subject, $"Field '{name}' on '{owner}' became narrower (length or scale).", Breaking: true));
            }
            else if (newField.Required && !oldField.Required && newField.DefaultValue is null)
            {
                changes.Add(new("field", subject, $"Field '{name}' on '{owner}' became required without a default value.", Breaking: true));
            }
        }

        foreach (var (name, newField) in next.Where(kv => !previous.ContainsKey(kv.Key)))
        {
            var breaking = newField.Required && newField.DefaultValue is null;
            changes.Add(new(
                "field",
                $"{owner}.{name}",
                breaking
                    ? $"Required field '{name}' without a default value was added to '{owner}'; existing rows cannot satisfy it."
                    : $"Field '{name}' was added to '{owner}'.",
                breaking));
        }
    }
}

/// <summary>The rules an upgrade must satisfy before the core applies it.</summary>
public static class AppUpgradeValidator
{
    /// <summary>An installed app and the manifest it was installed from, for dependency checks.</summary>
    public sealed record InstalledDependent(string AppId, string Version, AppManifest Manifest);

    public static List<string> Validate(
        AppManifest installed,
        AppManifest next,
        IReadOnlyList<SchemaChange> changes,
        IEnumerable<InstalledDependent> otherInstalledApps)
    {
        var errors = new List<string>();
        if (!SemverVersion.TryParse(installed.Version, out var from) || !SemverVersion.TryParse(next.Version, out var to))
        {
            errors.Add("Both the installed and the new version must be valid semantic versions.");
            return errors;
        }

        if (to <= from)
        {
            errors.Add(
                to == from
                    ? $"Version {to} of '{next.Id}' is already installed."
                    : $"Version {to} is older than the installed {from}; downgrades are not supported.");
            return errors;
        }

        errors.AddRange(ValidateMigrationDeclarations(next));

        var major = to.Major > from.Major;
        var breaking = changes.Where(c => c.Breaking).ToList();
        if (!major && breaking.Count > 0)
        {
            errors.Add(
                $"Version {to} is not a new major version but has breaking changes: {string.Join(" ", breaking.Select(c => c.Message))} "
                + $"Breaking changes require version {from.Major + 1}.0.0 or higher.");
        }

        if (major && MigrationPlanner.Select(next, installed.Version).Count == 0)
        {
            errors.Add(
                $"Major upgrade {from} -> {to} requires a data migration. Declare one in 'migrations' "
                + $"(from: {from}, to: {to}, run: <name>).");
        }

        if (major)
        {
            foreach (var dependent in otherInstalledApps)
            {
                if (!dependent.Manifest.Dependencies.TryGetValue(next.Id, out var rangeExpr))
                {
                    continue;
                }

                if (!SemverRange.TryParse(rangeExpr, out var range) || range is null || !range.IsSatisfiedBy(to))
                {
                    errors.Add(
                        $"Installed app '{dependent.AppId}' ({dependent.Version}) depends on '{next.Id}' {rangeExpr}, "
                        + $"which version {to} does not satisfy. Upgrade '{dependent.AppId}' first or together with it.");
                }
            }
        }

        return errors;
    }

    private static IEnumerable<string> ValidateMigrationDeclarations(AppManifest manifest)
    {
        _ = SemverVersion.TryParse(manifest.Version, out var current);
        foreach (var m in manifest.Migrations)
        {
            if (!SemverVersion.TryParse(m.From, out var from) || !SemverVersion.TryParse(m.To, out var to))
            {
                yield return $"Migration '{m.Run}' has an invalid version range ({m.From} -> {m.To}).";
                continue;
            }

            if (to <= from)
            {
                yield return $"Migration '{m.Run}' must move to a higher version ({m.From} -> {m.To}).";
            }

            if (to > current)
            {
                yield return $"Migration '{m.Run}' targets {to}, which is newer than the app's version {current}.";
            }

            if (string.IsNullOrWhiteSpace(m.Run))
            {
                yield return $"The migration {m.From} -> {m.To} does not name what to run ('run').";
            }
        }
    }
}

/// <summary>Chooses which migrations an upgrade runs.</summary>
public static class MigrationPlanner
{
    /// <summary>
    /// The migrations that move data from <paramref name="installedVersion"/> up to the manifest's version: those that
    /// start at or after the installed version and end at or before the new one, in the order they target.
    /// </summary>
    public static IReadOnlyList<MigrationDefinition> Select(AppManifest next, string installedVersion)
    {
        if (!SemverVersion.TryParse(installedVersion, out var installed) || !SemverVersion.TryParse(next.Version, out var target))
        {
            return [];
        }

        return next.Migrations
            .Where(m => SemverVersion.TryParse(m.From, out var from) && SemverVersion.TryParse(m.To, out var to)
                        && from >= installed && to <= target && to > from)
            .OrderBy(m => SemverVersion.Parse(m.To))
            .ThenBy(m => SemverVersion.Parse(m.From))
            .ToList();
    }
}
