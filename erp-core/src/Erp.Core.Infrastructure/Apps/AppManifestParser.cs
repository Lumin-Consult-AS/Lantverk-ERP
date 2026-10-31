using System.Globalization;
using Erp.Core.Domain.Apps;
using Erp.Core.Domain.Data;
using Erp.Core.Domain.Metadata;
using Erp.Core.Infrastructure.Metadata;
using YamlDotNet.Serialization;

namespace Erp.Core.Infrastructure.Apps;

/// <summary>
/// Parses <c>app.yaml</c> manifests and auxiliary package files (<c>pages/*.yaml</c>,
/// <c>permissions/*.yaml</c>, <c>i18n/*.yaml</c>) into an <see cref="AppManifest"/>.
/// </summary>
public static class AppManifestParser
{
    private static readonly HashSet<string> AllowedRootProperties = new(StringComparer.Ordinal)
    {
        "id",
        "version",
        "name",
        "publisher",
        "license",
        "sdk",
        "dependencies",
        "runtime",
        "objects",
        "extensions",
        "enums",
        "interfaces",
        "events",
        "pages",
        "permissionSets",
        "migrations",
        "attention",
        "workflows",
        "syncSets",
        "commands",
        "schedules"
    };

    private static readonly HashSet<string> AllowedPermissions = new(StringComparer.OrdinalIgnoreCase)
    {
        "read",
        "create",
        "update",
        "delete",
        "post",
        "approve"
    };

    public static AppManifest ParseManifestYaml(
        string appYamlContent,
        IReadOnlyDictionary<string, string>? packageFiles = null)
    {
        if (string.IsNullOrWhiteSpace(appYamlContent))
        {
            throw new AppManifestValidationException("Manifest 'app.yaml' cannot be empty.");
        }

        var deserializer = new DeserializerBuilder().Build();
        object? rootObj;
        try
        {
            rootObj = deserializer.Deserialize<object>(appYamlContent);
        }
        catch (Exception ex)
        {
            throw new AppManifestValidationException($"Invalid YAML syntax in 'app.yaml': {ex.Message}");
        }

        if (rootObj is not IDictionary<object, object> root)
        {
            throw new AppManifestValidationException("Manifest 'app.yaml' root must be a YAML mapping.");
        }

        var errors = new List<string>();

        // Validate against JSON schema root properties (additionalProperties: false)
        foreach (var keyObj in root.Keys)
        {
            var key = keyObj?.ToString() ?? string.Empty;
            if (!AllowedRootProperties.Contains(key))
            {
                errors.Add($"Unknown manifest property '{key}' is not allowed by app-manifest.schema.json.");
            }
        }

        var id = GetString(root, "id");
        var version = GetString(root, "version");
        var name = GetString(root, "name");

        if (string.IsNullOrWhiteSpace(id))
        {
            errors.Add("Manifest is missing required property 'id'.");
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            errors.Add("Manifest is missing required property 'version'.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add("Manifest is missing required property 'name'.");
        }

        if (errors.Count > 0)
        {
            throw new AppManifestValidationException(errors);
        }

        MetadataBundle metadataBundle;
        try
        {
            metadataBundle = YamlMetadataParser.Parse(appYamlContent);
        }
        catch (MetadataValidationException mex)
        {
            throw new AppManifestValidationException(mex.Message);
        }

        var manifest = new AppManifest
        {
            Id = id!,
            Version = version!,
            Name = name!,
            Publisher = GetString(root, "publisher"),
            License = GetString(root, "license"),
            Sdk = GetString(root, "sdk"),
            Metadata = metadataBundle
        };

        // Parse dependencies
        if (TryGetMap(root, "dependencies", out var depsMap))
        {
            foreach (var kvp in depsMap)
            {
                var depId = kvp.Key?.ToString() ?? string.Empty;
                var depRange = kvp.Value?.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(depId))
                {
                    manifest.Dependencies[depId] = depRange;
                }
            }
        }

        // Parse optional runtime section
        if (root.TryGetValue("runtime", out var runtimeObj) && runtimeObj is not null)
        {
            if (runtimeObj is not IDictionary<object, object> runtimeMap)
            {
                throw new AppManifestValidationException("Manifest 'runtime' section must be a mapping.");
            }

            var runtime = new AppRuntimeDefinition
            {
                Image = GetString(runtimeMap, "image") ?? string.Empty
            };

            if (TryGetMap(runtimeMap, "resources", out var resMap))
            {
                runtime.Resources = new AppResourceLimits
                {
                    Cpu = GetString(resMap, "cpu"),
                    Memory = GetString(resMap, "memory")
                };
            }

            if (runtimeMap.TryGetValue("egress", out var egressObj) && egressObj is IEnumerable<object> egressList)
            {
                runtime.Egress.AddRange(egressList.Select(e => e?.ToString() ?? string.Empty).Where(e => e.Length > 0));
            }

            manifest.Runtime = runtime;
        }

        // Parse event subscriptions
        if (TryGetMap(root, "events", out var eventsMap)
            && eventsMap.TryGetValue("subscribes", out var subsObj)
            && subsObj is IEnumerable<object> subsList)
        {
            foreach (var subItem in subsList)
            {
                if (subItem is string subStr)
                {
                    var (evtId, ver) = SplitEventSpec(subStr);
                    manifest.SubscribedEvents.Add(new EventSubscriptionDefinition
                    {
                        EventId = evtId,
                        Version = ver,
                        Handler = evtId
                    });
                }
                else if (subItem is IDictionary<object, object> subMap)
                {
                    foreach (var kvp in subMap)
                    {
                        var spec = kvp.Key?.ToString() ?? string.Empty;
                        var handler = kvp.Value?.ToString() ?? string.Empty;
                        var (evtId, ver) = SplitEventSpec(spec);
                        manifest.SubscribedEvents.Add(new EventSubscriptionDefinition
                        {
                            EventId = evtId,
                            Version = ver,
                            Handler = handler
                        });
                    }
                }
            }
        }

        // Parse pages list in manifest
        if (root.TryGetValue("pages", out var pagesObj) && pagesObj is IEnumerable<object> pagesList)
        {
            foreach (var pageItem in pagesList)
            {
                if (pageItem is string filePath)
                {
                    manifest.PageEntries.Add(new ManifestPageEntry { File = NormalizePath(filePath) });
                }
                else if (pageItem is IDictionary<object, object> pageEntryMap)
                {
                    var file = GetString(pageEntryMap, "file") ?? string.Empty;
                    var extend = GetString(pageEntryMap, "extend");
                    manifest.PageEntries.Add(new ManifestPageEntry
                    {
                        File = NormalizePath(file),
                        Extend = extend
                    });
                }
            }
        }

        // Parse permissionSets in manifest
        if (root.TryGetValue("permissionSets", out var permObj) && permObj is IEnumerable<object> permList)
        {
            foreach (var permItem in permList)
            {
                if (permItem is IDictionary<object, object> permMap)
                {
                    manifest.PermissionSets.Add(ParsePermissionSet(permMap, manifest.Id));
                }
            }
        }

        // Parse migrations
        if (root.TryGetValue("migrations", out var migObj) && migObj is IEnumerable<object> migList)
        {
            foreach (var migItem in migList)
            {
                if (migItem is IDictionary<object, object> migMap)
                {
                    manifest.Migrations.Add(new MigrationDefinition
                    {
                        From = GetString(migMap, "from") ?? string.Empty,
                        To = GetString(migMap, "to") ?? string.Empty,
                        Run = GetString(migMap, "run") ?? string.Empty
                    });
                }
            }
        }

        // Parse attention items
        if (root.TryGetValue("attention", out var attObj) && attObj is IEnumerable<object> attList)
        {
            foreach (var attItem in attList)
            {
                if (attItem is IDictionary<object, object> attMap)
                {
                    manifest.AttentionItems.Add(new AttentionDefinition
                    {
                        Id = GetString(attMap, "id") ?? string.Empty,
                        OwnerApp = manifest.Id,
                        Label = GetString(attMap, "label") ?? string.Empty,
                        Severity = GetString(attMap, "severity") ?? "info",
                        Source = TryGetMap(attMap, "source", out var src) ? ConvertYamlDictionary(src) : [],
                        Audience = TryGetMap(attMap, "audience", out var aud) ? ConvertYamlDictionary(aud) : null,
                        Action = TryGetMap(attMap, "action", out var act) ? ConvertYamlDictionary(act) : null
                    });
                }
            }
        }

        // Parse workflow defaults (approval flows attached to status transitions)
        if (root.TryGetValue("workflows", out var wfObj) && wfObj is IEnumerable<object> wfList)
        {
            foreach (var wf in wfList)
            {
                if (wf is IDictionary<object, object> wfMap)
                {
                    manifest.Workflows.Add(Erp.Core.Infrastructure.Workflow.WorkflowYamlParser.Parse(wfMap));
                }
            }
        }

        // Parse offline sync sets and commands
        if (root.TryGetValue("syncSets", out var syncObj) && syncObj is IEnumerable<object> syncList)
        {
            foreach (var item in syncList.OfType<IDictionary<object, object>>())
            {
                var set = new SyncSetDefinition
                {
                    Id = GetString(item, "id") ?? string.Empty,
                    OwnerApp = manifest.Id,
                    Label = GetString(item, "label") ?? string.Empty
                };

                if (item.TryGetValue("objects", out var objectsObj) && objectsObj is IEnumerable<object> objects)
                {
                    foreach (var entry in objects)
                    {
                        if (entry is string objectId)
                        {
                            set.Objects.Add(new SyncSetObjectDefinition { Object = objectId });
                        }
                        else if (entry is IDictionary<object, object> objectMap)
                        {
                            set.Objects.Add(new SyncSetObjectDefinition
                            {
                                Object = GetString(objectMap, "object") ?? string.Empty,
                                Filter = GetString(objectMap, "filter")
                            });
                        }
                    }
                }

                manifest.SyncSets.Add(set);
            }
        }

        if (root.TryGetValue("commands", out var cmdObj) && cmdObj is IEnumerable<object> cmdList)
        {
            foreach (var item in cmdList.OfType<IDictionary<object, object>>())
            {
                manifest.Commands.Add(new CommandDefinition
                {
                    Id = GetString(item, "id") ?? string.Empty,
                    OwnerApp = manifest.Id,
                    Label = GetString(item, "label") ?? string.Empty,
                    Handler = GetString(item, "handler") ?? string.Empty,
                    OnConflict = GetString(item, "onConflict") ?? CommandConflictRules.Reject
                });
            }
        }

        if (root.TryGetValue("schedules", out var scheduleObj) && scheduleObj is IEnumerable<object> scheduleList)
        {
            foreach (var item in scheduleList.OfType<IDictionary<object, object>>())
            {
                var at = GetString(item, "at");
                manifest.Schedules.Add(new ScheduleDefinition
                {
                    Id = GetString(item, "id") ?? string.Empty,
                    OwnerApp = manifest.Id,
                    Label = GetString(item, "label") ?? string.Empty,
                    CommandId = GetString(item, "command") ?? string.Empty,
                    RawAt = at,
                    At = at is not null && TimeOnly.TryParseExact(at, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var time)
                        ? time
                        : null,
                    EveryMinutes = GetInt(item, "everyMinutes")
                });
            }
        }

        // Load auxiliary files from package if provided
        if (packageFiles is not null)
        {
            LoadAuxiliaryPackageFiles(manifest, packageFiles, deserializer);
        }

        return manifest;
    }

    private static void LoadAuxiliaryPackageFiles(
        AppManifest manifest,
        IReadOnlyDictionary<string, string> packageFiles,
        IDeserializer deserializer)
    {
        // Load referenced pages
        foreach (var entry in manifest.PageEntries)
        {
            if (!TryGetPackageFile(packageFiles, entry.File, out var pageYaml))
            {
                throw new AppManifestValidationException(
                    $"Referenced page file '{entry.File}' in 'app.yaml' was not found in the app package.");
            }

            if (deserializer.Deserialize<object>(pageYaml) is not IDictionary<object, object> pageMap)
            {
                throw new AppManifestValidationException($"Page file '{entry.File}' must contain a YAML mapping.");
            }

            var extendTarget = entry.Extend ?? GetString(pageMap, "extend");
            if (!string.IsNullOrWhiteSpace(extendTarget))
            {
                manifest.PageExtensions.Add(ParsePageExtension(pageMap, extendTarget, manifest.Id));
            }
            else
            {
                manifest.Pages.Add(ParsePageDefinition(pageMap, manifest.Id, entry.File));
            }
        }

        // Load additional permission files in permissions/*.yaml
        foreach (var kvp in packageFiles)
        {
            var normKey = NormalizePath(kvp.Key);
            if (normKey.StartsWith("permissions/", StringComparison.OrdinalIgnoreCase)
                && (normKey.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || normKey.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)))
            {
                var permDoc = deserializer.Deserialize<object>(kvp.Value);
                if (permDoc is IDictionary<object, object> singlePermMap)
                {
                    var parsed = ParsePermissionSet(singlePermMap, manifest.Id);
                    if (manifest.PermissionSets.All(p => !string.Equals(p.Id, parsed.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        manifest.PermissionSets.Add(parsed);
                    }
                }
                else if (permDoc is IEnumerable<object> multiPermList)
                {
                    foreach (var item in multiPermList.OfType<IDictionary<object, object>>())
                    {
                        var parsed = ParsePermissionSet(item, manifest.Id);
                        if (manifest.PermissionSets.All(p => !string.Equals(p.Id, parsed.Id, StringComparison.OrdinalIgnoreCase)))
                        {
                            manifest.PermissionSets.Add(parsed);
                        }
                    }
                }
            }
            else if (normKey.StartsWith("i18n/", StringComparison.OrdinalIgnoreCase)
                     && (normKey.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) || normKey.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)))
            {
                var fileName = Path.GetFileNameWithoutExtension(normKey);
                if (deserializer.Deserialize<object>(kvp.Value) is IDictionary<object, object> i18nMap)
                {
                    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    FlattenTranslations(string.Empty, i18nMap, dict);
                    manifest.Translations[fileName] = dict;
                }
            }
        }
    }

    private static PageDefinition ParsePageDefinition(
        IDictionary<object, object> map,
        string ownerApp,
        string fallbackFile)
    {
        var id = GetString(map, "id") ?? $"{ownerApp}.{Path.GetFileNameWithoutExtension(fallbackFile)}";
        var page = new PageDefinition
        {
            Id = id,
            OwnerApp = ownerApp,
            Type = GetString(map, "type") ?? "list",
            Object = GetString(map, "object") ?? string.Empty,
            Title = GetString(map, "title")
        };

        if (map.TryGetValue("columns", out var colsObj) && colsObj is IEnumerable<object> colsList)
        {
            page.Columns.AddRange(colsList.Select(c => c?.ToString() ?? string.Empty).Where(c => c.Length > 0));
        }
        else if (map.TryGetValue("fields", out var fieldsObj) && fieldsObj is IEnumerable<object> fieldsList)
        {
            page.Columns.AddRange(fieldsList.Select(c => c?.ToString() ?? string.Empty).Where(c => c.Length > 0));
        }

        if (map.TryGetValue("groups", out var groupsObj) && groupsObj is IEnumerable<object> groupsList)
        {
            foreach (var grpMap in groupsList.OfType<IDictionary<object, object>>())
            {
                var grp = new PageGroupDefinition
                {
                    Label = GetString(grpMap, "label") ?? string.Empty
                };
                if (grpMap.TryGetValue("fields", out var gfObj) && gfObj is IEnumerable<object> gfList)
                {
                    grp.Fields.AddRange(gfList.Select(f => f?.ToString() ?? string.Empty).Where(f => f.Length > 0));
                }
                page.Groups.Add(grp);
            }
        }

        if (map.TryGetValue("tabs", out var tabsObj) && tabsObj is IEnumerable<object> tabsList)
        {
            foreach (var tabMap in tabsList.OfType<IDictionary<object, object>>())
            {
                page.Tabs.Add(ParsePageTab(tabMap));
            }
        }

        if (map.TryGetValue("actions", out var actsObj) && actsObj is IEnumerable<object> actsList)
        {
            foreach (var actMap in actsList.OfType<IDictionary<object, object>>())
            {
                page.Actions.Add(new PageActionDefinition
                {
                    Id = GetString(actMap, "id") ?? string.Empty,
                    Label = GetString(actMap, "label") ?? string.Empty,
                    Transition = GetString(actMap, "transition"),
                    Placement = GetString(actMap, "placement") ?? "primary"
                });
            }
        }

        return page;
    }

    private static PageExtensionDefinition ParsePageExtension(
        IDictionary<object, object> map,
        string extendTarget,
        string extendingApp)
    {
        var ext = new PageExtensionDefinition
        {
            Extend = extendTarget,
            ExtendingApp = extendingApp
        };

        if (map.TryGetValue("tabs", out var tabsObj) && tabsObj is IEnumerable<object> tabsList)
        {
            foreach (var tabMap in tabsList.OfType<IDictionary<object, object>>())
            {
                ext.Tabs.Add(ParsePageTab(tabMap));
            }
        }

        if (map.TryGetValue("fields", out var fieldsObj) && fieldsObj is IEnumerable<object> fieldsList)
        {
            foreach (var fieldMap in fieldsList.OfType<IDictionary<object, object>>())
            {
                ext.Fields.Add(new PageFieldPlacement
                {
                    Field = GetString(fieldMap, "field") ?? string.Empty,
                    After = GetString(fieldMap, "after"),
                    Group = GetString(fieldMap, "group")
                });
            }
        }

        return ext;
    }

    private static PageTabDefinition ParsePageTab(IDictionary<object, object> tabMap)
    {
        var tab = new PageTabDefinition
        {
            Label = GetString(tabMap, "label") ?? string.Empty
        };

        if (TryGetMap(tabMap, "list", out var listMap))
        {
            tab.Object = GetString(listMap, "object");
            if (TryGetMap(listMap, "filter", out var filterMap))
            {
                tab.Filter = ConvertYamlDictionary(filterMap);
            }
            if (listMap.TryGetValue("fields", out var lfObj) && lfObj is IEnumerable<object> lfList)
            {
                tab.Fields.AddRange(lfList.Select(f => f?.ToString() ?? string.Empty).Where(f => f.Length > 0));
            }
        }
        else
        {
            tab.Object = GetString(tabMap, "object");
            if (tabMap.TryGetValue("fields", out var tfObj) && tfObj is IEnumerable<object> tfList)
            {
                tab.Fields.AddRange(tfList.Select(f => f?.ToString() ?? string.Empty).Where(f => f.Length > 0));
            }
        }

        return tab;
    }

    private static PermissionSetDefinition ParsePermissionSet(IDictionary<object, object> permMap, string ownerApp)
    {
        var ps = new PermissionSetDefinition
        {
            Id = GetString(permMap, "id") ?? string.Empty,
            OwnerApp = ownerApp,
            Label = GetString(permMap, "label") ?? string.Empty
        };

        if (TryGetMap(permMap, "grants", out var grantsMap))
        {
            foreach (var kvp in grantsMap)
            {
                var objectId = kvp.Key?.ToString() ?? string.Empty;
                var rights = new List<string>();
                if (kvp.Value is IEnumerable<object> rList)
                {
                    foreach (var rObj in rList)
                    {
                        var rStr = rObj?.ToString()?.Trim().ToLowerInvariant() ?? string.Empty;
                        if (!AllowedPermissions.Contains(rStr))
                        {
                            throw new AppManifestValidationException(
                                $"Permission set '{ps.Id}' grants unsupported permission '{rStr}' on '{objectId}'.");
                        }
                        rights.Add(rStr);
                    }
                }
                ps.Grants[objectId] = rights;
            }
        }

        return ps;
    }

    private static void FlattenTranslations(
        string prefix,
        IDictionary<object, object> map,
        Dictionary<string, string> target)
    {
        foreach (var kvp in map)
        {
            var key = kvp.Key?.ToString() ?? string.Empty;
            var fullKey = string.IsNullOrEmpty(prefix) ? key : $"{prefix}.{key}";
            if (kvp.Value is IDictionary<object, object> nested)
            {
                FlattenTranslations(fullKey, nested, target);
            }
            else if (kvp.Value is not null)
            {
                target[fullKey] = Convert.ToString(kvp.Value, CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }
    }

    private static Dictionary<string, object?> ConvertYamlDictionary(IDictionary<object, object> map)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in map)
        {
            var key = kvp.Key?.ToString() ?? string.Empty;
            result[key] = kvp.Value is IDictionary<object, object> childMap
                ? ConvertYamlDictionary(childMap)
                : kvp.Value;
        }
        return result;
    }

    private static bool TryGetPackageFile(
        IReadOnlyDictionary<string, string> files,
        string relativePath,
        out string content)
    {
        var norm = NormalizePath(relativePath);
        if (files.TryGetValue(norm, out var exact))
        {
            content = exact;
            return true;
        }

        foreach (var kvp in files)
        {
            if (string.Equals(NormalizePath(kvp.Key), norm, StringComparison.OrdinalIgnoreCase))
            {
                content = kvp.Value;
                return true;
            }
        }

        content = string.Empty;
        return false;
    }

    public static string NormalizePath(string path)
        => path.Replace('\\', '/').TrimStart('/');

    private static (string EventId, int Version) SplitEventSpec(string spec)
    {
        var parts = spec.Split('@', 2);
        var ver = parts.Length == 2 && int.TryParse(parts[1], CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 1;
        return (spec, ver);
    }

    private static bool TryGetMap(IDictionary<object, object> map, string key, out IDictionary<object, object> result)
    {
        if (map.TryGetValue(key, out var val) && val is IDictionary<object, object> dict)
        {
            result = dict;
            return true;
        }

        result = new Dictionary<object, object>();
        return false;
    }

    private static string? GetString(IDictionary<object, object> map, string key)
        => map.TryGetValue(key, out var val) ? val?.ToString() : null;

    private static int? GetInt(IDictionary<object, object> map, string key)
        => map.TryGetValue(key, out var val) && int.TryParse(val?.ToString(), System.Globalization.CultureInfo.InvariantCulture, out var number) ? number : null;
}
