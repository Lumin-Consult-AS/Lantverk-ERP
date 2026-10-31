using System.Text.Json;
using Erp.Core.Domain.Metadata;

namespace Erp.Core.Infrastructure.Apps;

/// <summary>
/// The metadata an upgrade applies while its data migrations run: everything in the new version plus what only the old
/// version had (removed objects, fields, enum values, events). That lets a migration read the old shape and write the new
/// one through the data API. Once the migrations have completed the new version's metadata is applied on its own and
/// what was removed is retired (ADR-0013).
/// </summary>
internal static class AppMetadataUnion
{
    public static (MetadataBundle Union, bool Differs) Build(MetadataBundle old, MetadataBundle next, JsonSerializerOptions json)
    {
        var union = Clone(next, json);
        var differs = false;

        foreach (var oldObject in old.Objects)
        {
            var current = union.Objects.FirstOrDefault(o => string.Equals(o.Id, oldObject.Id, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                union.Objects.Add(Clone(oldObject, json));
                differs = true;
                continue;
            }

            differs |= AddMissingFields(current.Fields, oldObject.Fields, json);
        }

        foreach (var oldExtension in old.Extensions)
        {
            var current = union.Extensions.FirstOrDefault(e => string.Equals(e.TargetObject, oldExtension.TargetObject, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                union.Extensions.Add(Clone(oldExtension, json));
                differs = true;
                continue;
            }

            differs |= AddMissingFields(current.Fields, oldExtension.Fields, json);
        }

        foreach (var oldEnum in old.Enums)
        {
            var current = union.Enums.FirstOrDefault(e => string.Equals(e.Id, oldEnum.Id, StringComparison.OrdinalIgnoreCase));
            if (current is null)
            {
                union.Enums.Add(Clone(oldEnum, json));
                differs = true;
                continue;
            }

            foreach (var value in oldEnum.Values.Except(current.Values, StringComparer.OrdinalIgnoreCase).ToList())
            {
                current.Values.Add(value);
                differs = true;
            }
        }

        foreach (var oldEvent in old.Events.Where(e => !union.Events.Any(n => string.Equals(n.Id, e.Id, StringComparison.OrdinalIgnoreCase))))
        {
            union.Events.Add(Clone(oldEvent, json));
            differs = true;
        }

        foreach (var oldInterface in old.Interfaces.Where(i => !union.Interfaces.Any(n => string.Equals(n.Id, i.Id, StringComparison.OrdinalIgnoreCase))))
        {
            union.Interfaces.Add(Clone(oldInterface, json));
            differs = true;
        }

        return (union, differs);
    }

    /// <summary>Old-only fields are kept optional: new code does not have to supply a value for a field it no longer knows.</summary>
    private static bool AddMissingFields(List<FieldDefinition> target, IEnumerable<FieldDefinition> old, JsonSerializerOptions json)
    {
        var added = false;
        foreach (var field in old.Where(f => !target.Any(t => string.Equals(t.Name, f.Name, StringComparison.OrdinalIgnoreCase))))
        {
            var copy = Clone(field, json);
            copy.Required = false;
            copy.RequiredWhen = null;
            target.Add(copy);
            added = true;
        }

        return added;
    }

    private static T Clone<T>(T value, JsonSerializerOptions json) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, json), json)!;
}
