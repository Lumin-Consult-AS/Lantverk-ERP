using System.Text.RegularExpressions;
using Erp.Core.Domain.Apps;
using Erp.Core.Domain.Metadata;

namespace Erp.Core.Infrastructure.Apps;

/// <summary>
/// Enforces all structural and tenant-state installation rules for <see cref="AppManifest"/>.
/// </summary>
public static class AppManifestValidator
{
    public static readonly SemverVersion CurrentPlatformSdkVersion = new(1, 0, 0);

    private static readonly Regex AppIdRegex = new(
        @"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HostnameRegex = new(
        @"^(\*\.)?[a-zA-Z0-9]([a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?(\.[a-zA-Z0-9]([a-zA-Z0-9\-]{0,61}[a-zA-Z0-9])?)+(:[0-9]{1,5})?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> BuiltInCoreEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "core.workflow.started@1",
        "core.workflow.stepcompleted@1",
        "core.workflow.completed@1",
        "core.inbox.received@1"
    };

    public static void ValidateForInstallation(
        AppManifest manifest,
        IReadOnlyDictionary<string, string> installedAppVersions,
        IReadOnlyDictionary<string, ObjectDefinition> existingObjects,
        IReadOnlySet<string> existingPublishedEvents)
    {
        var errors = new List<string>();

        if (!AppIdRegex.IsMatch(manifest.Id))
        {
            errors.Add($"App ID '{manifest.Id}' is invalid. Expected lowercase dot-separated identifier (e.g. 'core.parties').");
        }

        if (!SemverVersion.TryParse(manifest.Version, out _))
        {
            errors.Add($"App version '{manifest.Version}' is not a valid semantic version (MAJOR.MINOR.PATCH).");
        }

        if (!string.IsNullOrWhiteSpace(manifest.Sdk))
        {
            if (!SemverRange.TryParse(manifest.Sdk, out var sdkRange) || sdkRange is null)
            {
                errors.Add($"Manifest 'sdk' range '{manifest.Sdk}' is not a valid semver range.");
            }
            else if (!sdkRange.IsSatisfiedBy(CurrentPlatformSdkVersion))
            {
                errors.Add($"Manifest requires SDK '{manifest.Sdk}', which is not satisfied by platform SDK {CurrentPlatformSdkVersion}.");
            }
        }

        // 1. Validate dependencies against installed apps in the tenant
        foreach (var (depAppId, rangeExpr) in manifest.Dependencies)
        {
            if (!SemverRange.TryParse(rangeExpr, out var depRange) || depRange is null)
            {
                errors.Add($"Dependency '{depAppId}' specifies an invalid version range '{rangeExpr}'.");
                continue;
            }

            if (!installedAppVersions.TryGetValue(depAppId, out var installedVerStr))
            {
                errors.Add($"Required dependency '{depAppId}' ({rangeExpr}) is not installed in this tenant.");
                continue;
            }

            if (!SemverVersion.TryParse(installedVerStr, out var installedVer) || !depRange.IsSatisfiedBy(installedVer))
            {
                errors.Add(
                    $"Installed version '{installedVerStr}' of dependency '{depAppId}' does not satisfy required range '{rangeExpr}'.");
            }
        }

        // 2. Validate prefix rules on owned objects, enums, events, and permission sets
        foreach (var obj in manifest.Metadata.Objects)
        {
            if (!manifest.HasValidAppPrefix(obj.Id))
            {
                errors.Add(
                    $"Object '{obj.Id}' does not match owning app '{manifest.Id}' prefix.");
            }

            foreach (var field in obj.Fields)
            {
                if (!string.Equals(field.OwnerApp, manifest.Id, StringComparison.OrdinalIgnoreCase)
                    && !manifest.HasValidAppPrefix(field.OwnerApp))
                {
                    errors.Add(
                        $"Field '{field.Name}' on '{obj.Id}' has invalid ownerApp '{field.OwnerApp}' for app '{manifest.Id}'.");
                }
            }
        }

        foreach (var enumDef in manifest.Metadata.Enums)
        {
            if (!manifest.HasValidAppPrefix(enumDef.Id))
            {
                errors.Add(
                    $"Enum '{enumDef.Id}' does not match owning app '{manifest.Id}' prefix.");
            }
        }

        foreach (var evt in manifest.Metadata.Events)
        {
            if (!manifest.HasValidAppPrefix(evt.Id))
            {
                errors.Add(
                    $"Published event '{evt.Id}' does not match owning app '{manifest.Id}' prefix.");
            }
        }

        foreach (var permSet in manifest.PermissionSets)
        {
            if (!manifest.HasValidAppPrefix(permSet.Id))
            {
                errors.Add(
                    $"Permission set '{permSet.Id}' does not match owning app '{manifest.Id}' prefix.");
            }
        }

        // 3. Validate extensions (target object must exist and must NOT be sealed)
        var manifestObjectsById = manifest.Metadata.Objects.ToDictionary(o => o.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var ext in manifest.Metadata.Extensions)
        {
            if (!existingObjects.TryGetValue(ext.TargetObject, out var targetObj)
                && !manifestObjectsById.TryGetValue(ext.TargetObject, out targetObj))
            {
                errors.Add(
                    $"Extension target object '{ext.TargetObject}' does not exist in this tenant.");
                continue;
            }

            if (targetObj.IsSealed)
            {
                errors.Add(
                    $"Cannot extend object '{ext.TargetObject}' because it is marked as sealed by '{targetObj.OwnerApp}'.");
            }
        }

        // 3b. Validate workflows: owned by this app, valid definition, and every transition's workflow is defined
        var manifestWorkflowIds = manifest.Workflows.Select(w => w.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var workflow in manifest.Workflows)
        {
            if (!manifest.HasValidAppPrefix(workflow.Id))
            {
                errors.Add($"Workflow '{workflow.Id}' does not match owning app '{manifest.Id}' prefix.");
            }

            if (!existingObjects.TryGetValue(workflow.Object, out var workflowObject)
                && !manifestObjectsById.TryGetValue(workflow.Object, out workflowObject))
            {
                errors.Add($"Workflow '{workflow.Id}' refers to unknown object '{workflow.Object}'.");
                continue;
            }

            errors.AddRange(Erp.Core.Domain.Workflow.WorkflowDefinitionValidator.Validate(workflow, workflowObject));
        }

        foreach (var obj in manifest.Metadata.Objects)
        {
            foreach (var (transitionName, transition) in obj.Status?.Transitions ?? [])
            {
                if (!string.IsNullOrWhiteSpace(transition.Workflow) && !manifestWorkflowIds.Contains(transition.Workflow))
                {
                    errors.Add(
                        $"Transition '{transitionName}' on '{obj.Id}' refers to workflow '{transition.Workflow}', which this app does not define.");
                }
            }
        }

        // 3c. Validate offline sync sets and commands (ADR-0014)
        foreach (var set in manifest.SyncSets)
        {
            if (!manifest.HasValidAppPrefix(set.Id))
            {
                errors.Add($"Sync set '{set.Id}' does not match owning app '{manifest.Id}' prefix.");
            }

            if (set.Objects.Count == 0)
            {
                errors.Add($"Sync set '{set.Id}' must list at least one object.");
            }

            foreach (var entry in set.Objects)
            {
                if (!existingObjects.ContainsKey(entry.Object) && !manifestObjectsById.ContainsKey(entry.Object))
                {
                    errors.Add($"Sync set '{set.Id}' refers to unknown object '{entry.Object}'.");
                }

                if (!string.IsNullOrWhiteSpace(entry.Filter))
                {
                    try
                    {
                        _ = Erp.Core.Infrastructure.Data.FilterExpressionParser.Parse(
                            entry.Filter.Replace("{me}", "00000000-0000-0000-0000-000000000000").Replace("{today}", "2000-01-01"));
                    }
                    catch (Erp.Core.Domain.Data.RecordValidationException ex)
                    {
                        errors.Add($"Sync set '{set.Id}' has an invalid filter on '{entry.Object}': {ex.Message}");
                    }
                }
            }
        }

        foreach (var command in manifest.Commands)
        {
            if (!manifest.HasValidAppPrefix(command.Id))
            {
                errors.Add($"Command '{command.Id}' does not match owning app '{manifest.Id}' prefix.");
            }

            if (string.IsNullOrWhiteSpace(command.Handler))
            {
                errors.Add($"Command '{command.Id}' must name its handler.");
            }

            if (!CommandConflictRules.All.Contains(command.OnConflict, StringComparer.OrdinalIgnoreCase))
            {
                errors.Add($"Command '{command.Id}' has unknown conflict rule '{command.OnConflict}' (use reject, adjust or manual).");
            }

            if (manifest.IsDeclarative)
            {
                errors.Add($"App '{manifest.Id}' is purely declarative (no 'runtime' section) and cannot declare commands.");
            }
        }

        // 3d. Validate schedules: a declared command on a timetable
        foreach (var schedule in manifest.Schedules)
        {
            if (!manifest.HasValidAppPrefix(schedule.Id))
            {
                errors.Add($"Schedule '{schedule.Id}' does not match owning app '{manifest.Id}' prefix.");
            }

            if (!manifest.Commands.Any(c => string.Equals(c.Id, schedule.CommandId, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"Schedule '{schedule.Id}' runs command '{schedule.CommandId}', which this app does not declare.");
            }

            if (schedule.RawAt is not null && schedule.At is null)
            {
                errors.Add($"Schedule '{schedule.Id}' has an invalid 'at' time (use HH:mm, UTC).");
            }
            else if ((schedule.At is null) == (schedule.EveryMinutes is null))
            {
                errors.Add($"Schedule '{schedule.Id}' must have exactly one of 'at' (HH:mm, UTC) and 'everyMinutes'.");
            }
            else if (schedule.EveryMinutes is < 5)
            {
                errors.Add($"Schedule '{schedule.Id}' has 'everyMinutes' below the minimum of 5.");
            }

            if (manifest.IsDeclarative)
            {
                errors.Add($"App '{manifest.Id}' is purely declarative (no 'runtime' section) and cannot declare schedules.");
            }
        }

        // 4. Validate subscribed events (must exist in tenant or built-in core events)
        var selfPublishedEvents = manifest.Metadata.Events
            .Select(e => e.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var sub in manifest.SubscribedEvents)
        {
            if (!existingPublishedEvents.Contains(sub.EventId)
                && !selfPublishedEvents.Contains(sub.EventId)
                && !BuiltInCoreEvents.Contains(sub.EventId))
            {
                errors.Add(
                    $"Subscribed event '{sub.EventId}' is not published by any installed app in this tenant.");
            }
        }

        // 5. Declarative app vs runtime rules + egress whitelist validation
        if (manifest.IsDeclarative)
        {
            if (manifest.SubscribedEvents.Count > 0)
            {
                errors.Add(
                    $"App '{manifest.Id}' is purely declarative (no 'runtime' section) and cannot declare event subscribers.");
            }

            if (manifest.Migrations.Count > 0)
            {
                errors.Add(
                    $"App '{manifest.Id}' is purely declarative (no 'runtime' section) and cannot declare code migrations.");
            }
        }
        else
        {
            var runtime = manifest.Runtime!;
            if (string.IsNullOrWhiteSpace(runtime.Image))
            {
                errors.Add("Manifest 'runtime.image' is required when 'runtime' section is present.");
            }

            foreach (var egressEntry in runtime.Egress)
            {
                if (!IsValidEgressWhitelistEntry(egressEntry))
                {
                    errors.Add(
                        $"Invalid egress whitelist entry '{egressEntry}'. Only explicit hostnames or 'https://' origins are allowed (wildcards like '*' are forbidden).");
                }
            }
        }

        if (errors.Count > 0)
        {
            throw new AppManifestValidationException(errors);
        }
    }

    private static bool IsValidEgressWhitelistEntry(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            return false;
        }

        var trimmed = entry.Trim();
        if (trimmed is "*" or "0.0.0.0" or "::" || trimmed.Contains("*.*", StringComparison.Ordinal))
        {
            return false;
        }

        // Only treat the entry as an origin when it has an explicit scheme; otherwise "host:443"
        // would parse as a URI whose scheme is the hostname.
        if (trimmed.Contains("://", StringComparison.Ordinal)
            && Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(uri.Host)
                   && uri.Host != "*"
                   && uri.Host != "0.0.0.0"
                   && HostnameRegex.IsMatch(uri.Host);
        }

        return HostnameRegex.IsMatch(trimmed);
    }
}

