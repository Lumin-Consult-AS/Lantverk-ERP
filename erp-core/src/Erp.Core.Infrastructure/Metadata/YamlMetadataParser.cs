using System.Globalization;
using Erp.Core.Domain.Data;
using Erp.Core.Domain.Metadata;
using YamlDotNet.Serialization;

namespace Erp.Core.Infrastructure.Metadata;

/// <summary>
/// Parses YAML metadata definitions (either a single object definition or a full app manifest bundle)
/// into a validated <see cref="MetadataBundle"/>.
/// </summary>
public static class YamlMetadataParser
{
    public static MetadataBundle Parse(string yamlContent)
    {
        if (string.IsNullOrWhiteSpace(yamlContent))
        {
            throw new MetadataValidationException("YAML metadata content cannot be empty.");
        }

        var deserializer = new DeserializerBuilder().Build();
        var rootObj = deserializer.Deserialize<object>(yamlContent);
        if (rootObj is not IDictionary<object, object> root)
        {
            throw new MetadataValidationException("YAML root must be a mapping.");
        }

        // Check if this is a single-object YAML (has 'fields' at the root and no 'objects' section)
        if (root.ContainsKey("fields") && !root.ContainsKey("objects"))
        {
            var objectId = GetString(root, "id")
                ?? throw new MetadataValidationException("Object YAML must specify 'id' (e.g. 'demo.contactnote').");
            var (schema, _) = IdentifierNaming.SplitObjectId(objectId);
            var ownerApp = GetString(root, "ownerApp") ?? schema;
            var version = GetString(root, "version") ?? "1.0.0";

            var singleObj = ParseObjectDefinition(objectId, ownerApp, version, root);
            return new MetadataBundle
            {
                AppId = ownerApp,
                Version = version,
                Objects = [singleObj]
            };
        }

        var appId = GetString(root, "id") ?? GetString(root, "appId") ?? "core";
        var bundleVersion = GetString(root, "version") ?? "1.0.0";

        var bundle = new MetadataBundle
        {
            AppId = appId,
            Version = bundleVersion
        };

        // Parse enums
        if (TryGetMap(root, "enums", out var enumsMap))
        {
            foreach (var kvp in enumsMap)
            {
                var enumId = kvp.Key?.ToString() ?? string.Empty;
                var values = new List<string>();
                if (kvp.Value is IEnumerable<object> list)
                {
                    values.AddRange(list.Select(v => v?.ToString() ?? string.Empty).Where(v => v.Length > 0));
                }

                bundle.Enums.Add(new EnumDefinition
                {
                    Id = enumId,
                    OwnerApp = appId,
                    Values = values
                });
            }
        }

        // Parse objects
        if (TryGetMap(root, "objects", out var objectsMap))
        {
            foreach (var kvp in objectsMap)
            {
                var objectId = kvp.Key?.ToString() ?? string.Empty;
                if (kvp.Value is not IDictionary<object, object> objNode)
                {
                    throw new MetadataValidationException($"Object definition '{objectId}' must be a mapping.");
                }

                bundle.Objects.Add(ParseObjectDefinition(objectId, appId, bundleVersion, objNode));
            }
        }

        // Parse extensions
        if (TryGetMap(root, "extensions", out var extensionsMap))
        {
            foreach (var kvp in extensionsMap)
            {
                var targetObjectId = kvp.Key?.ToString() ?? string.Empty;
                if (kvp.Value is not IDictionary<object, object> extNode)
                {
                    throw new MetadataValidationException($"Extension for '{targetObjectId}' must be a mapping.");
                }

                var extFields = ParseFields(extNode, appId);
                bundle.Extensions.Add(new ObjectExtensionDefinition
                {
                    TargetObject = targetObjectId,
                    ExtendingApp = appId,
                    Fields = extFields
                });
            }
        }

        // Parse interfaces
        if (TryGetMap(root, "interfaces", out var interfacesMap))
        {
            foreach (var kvp in interfacesMap)
            {
                var interfaceId = kvp.Key?.ToString() ?? string.Empty;
                var iface = new InterfaceDefinition
                {
                    Id = interfaceId,
                    OwnerApp = appId
                };

                if (kvp.Value is IDictionary<object, object> ifaceNode
                    && ifaceNode.TryGetValue("operations", out var opsObj)
                    && opsObj is IEnumerable<object> opsList)
                {
                    foreach (var opItem in opsList)
                    {
                        if (opItem is string opName)
                        {
                            iface.Operations.Add(new InterfaceOperationDefinition { Name = opName });
                        }
                        else if (opItem is IDictionary<object, object> opMap)
                        {
                            iface.Operations.Add(new InterfaceOperationDefinition
                            {
                                Name = GetString(opMap, "name") ?? string.Empty,
                                InputType = GetString(opMap, "input"),
                                OutputType = GetString(opMap, "output")
                            });
                        }
                    }
                }

                bundle.Interfaces.Add(iface);
            }
        }

        // Parse events
        if (TryGetMap(root, "events", out var eventsMap)
            && eventsMap.TryGetValue("publishes", out var publishesObj)
            && publishesObj is IEnumerable<object> publishesList)
        {
            foreach (var pubItem in publishesList)
            {
                if (pubItem is string eventSpec)
                {
                    var (eventId, ver) = ParseEventSpec(eventSpec);
                    bundle.Events.Add(new EventDefinition
                    {
                        Id = eventId,
                        OwnerApp = appId,
                        Version = ver
                    });
                }
                else if (pubItem is IDictionary<object, object> eventMap)
                {
                    var spec = GetString(eventMap, "id") ?? string.Empty;
                    var (eventId, ver) = ParseEventSpec(spec);
                    bundle.Events.Add(new EventDefinition
                    {
                        Id = eventId,
                        OwnerApp = appId,
                        Version = ver,
                        PayloadFields = ParseFields(eventMap, appId)
                    });
                }
            }
        }

        return bundle;
    }

    private static ObjectDefinition ParseObjectDefinition(
        string objectId,
        string ownerApp,
        string version,
        IDictionary<object, object> node)
    {
        _ = IdentifierNaming.SplitObjectId(objectId);

        var effectiveOwnerApp = GetString(node, "ownerApp") ?? ownerApp;

        var obj = new ObjectDefinition
        {
            Id = objectId,
            OwnerApp = effectiveOwnerApp,
            Version = GetString(node, "version") ?? version,
            Label = GetString(node, "label") ?? objectId,
            IsSealed = GetBool(node, "sealed") ?? false,
            IsCompanyScoped = GetBool(node, "companyScoped") ?? true,
            Fields = ParseFields(node, effectiveOwnerApp)
        };

        if (node.TryGetValue("keys", out var keysObj) && keysObj is IEnumerable<object> keysList)
        {
            var idx = 1;
            foreach (var keyItem in keysList)
            {
                if (keyItem is IDictionary<object, object> keyMap)
                {
                    var keyFields = new List<string>();
                    if (keyMap.TryGetValue("fields", out var fObj) && fObj is IEnumerable<object> fList)
                    {
                        keyFields.AddRange(fList.Select(f => f?.ToString() ?? string.Empty).Where(f => f.Length > 0));
                    }

                    obj.Keys.Add(new UniqueKeyDefinition
                    {
                        Name = GetString(keyMap, "name") ?? $"key_{idx}",
                        Fields = keyFields,
                        ScopeToCompany = GetBool(keyMap, "scopeToCompany") ?? true
                    });
                    idx++;
                }
            }
        }

        return obj;
    }

    private static List<FieldDefinition> ParseFields(IDictionary<object, object> parentNode, string ownerApp)
    {
        var fields = new List<FieldDefinition>();
        if (!TryGetMap(parentNode, "fields", out var fieldsMap))
        {
            return fields;
        }

        foreach (var kvp in fieldsMap)
        {
            var fieldName = kvp.Key?.ToString() ?? string.Empty;
            if (kvp.Value is not IDictionary<object, object> fNode)
            {
                throw new MetadataValidationException($"Field '{fieldName}' must be a mapping.");
            }

            var typeStr = GetString(fNode, "type") ?? "string";
            var fieldType = ParseFieldType(typeStr);

            var onDeleteStr = GetString(fNode, "onDelete") ?? "restrict";
            var onDelete = onDeleteStr.ToLowerInvariant() switch
            {
                "cascade" => RelationDeleteBehavior.Cascade,
                "setnull" or "set_null" => RelationDeleteBehavior.SetNull,
                _ => RelationDeleteBehavior.Restrict
            };

            fields.Add(new FieldDefinition
            {
                Name = fieldName,
                OwnerApp = GetString(fNode, "ownerApp") ?? ownerApp,
                Type = fieldType,
                Label = GetString(fNode, "label"),
                Required = GetBool(fNode, "required") ?? false,
                DefaultValue = fNode.TryGetValue("default", out var defVal) ? defVal : null,
                MaxLength = GetInt(fNode, "maxLength") ?? GetInt(fNode, "length"),
                Scale = GetInt(fNode, "scale"),
                TargetObject = GetString(fNode, "to") ?? GetString(fNode, "targetObject"),
                OnDelete = onDelete,
                EnumType = GetString(fNode, "of") ?? GetString(fNode, "enumType")
            });
        }

        return fields;
    }

    private static FieldType ParseFieldType(string typeStr) => typeStr.Trim().ToLowerInvariant() switch
    {
        "string" => FieldType.String,
        "text" => FieldType.Text,
        "integer" or "int" or "long" => FieldType.Integer,
        "decimal" => FieldType.Decimal,
        "boolean" or "bool" => FieldType.Boolean,
        "date" => FieldType.Date,
        "datetime" or "timestamp" => FieldType.DateTime,
        "uuid" or "guid" => FieldType.Uuid,
        "reference" => FieldType.Reference,
        "enum" => FieldType.Enum,
        "json" or "jsonb" => FieldType.Json,
        "money" => FieldType.Money,
        "blob" or "blob-reference" or "blob_reference" => FieldType.Blob,
        _ => throw new MetadataValidationException($"Unsupported field type '{typeStr}'.")
    };

    private static (string EventId, int Version) ParseEventSpec(string spec)
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

    private static bool? GetBool(IDictionary<object, object> map, string key)
        => map.TryGetValue(key, out var val) && bool.TryParse(val?.ToString(), out var b) ? b : null;

    private static int? GetInt(IDictionary<object, object> map, string key)
        => map.TryGetValue(key, out var val) && int.TryParse(val?.ToString(), CultureInfo.InvariantCulture, out var i) ? i : null;
}

