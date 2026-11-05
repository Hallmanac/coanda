using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using NJsonSchema;

namespace Coanda.Tests.Sdk.Contract;

/// <summary>
/// Compares a schema document with the contract type that is it, as the type is written and read
/// with the given options, and lists every disagreement, in either direction and at every level:
/// a property the type writes that the schema does not define, a property the schema defines that
/// the type does not read, a property one requires and the other does not, an object one leaves
/// open and the other closes, a kind one has and the other lacks, and an enumerated value one
/// holds and the other does not.
/// </summary>
public static class ContractShape
{
    public static IReadOnlyList<string> Disagreements(JsonSchema schema, Type type, JsonSerializerOptions options)
    {
        var found = new List<string>();
        Compare(schema, type, options, "#", found);
        return found;
    }

    private static void Compare(JsonSchema schema, Type type, JsonSerializerOptions options, string path, List<string> found)
    {
        schema = schema.ActualSchema;
        type = Nullable.GetUnderlyingType(type) ?? type;
        JsonTypeInfo info = options.GetTypeInfo(type);

        if (info.PolymorphismOptions is { } polymorphism)
        {
            CompareKinds(schema, polymorphism, options, path, found);
            return;
        }

        switch (info.Kind)
        {
            case JsonTypeInfoKind.Object:
                CompareObject(schema, info, options, path, discriminator: null, found);
                break;
            case JsonTypeInfoKind.Enumerable:
                if (schema.Item is null)
                {
                    found.Add($"{path}: the type is a list, and the schema defines no items.");
                }
                else
                {
                    Compare(schema.Item, info.ElementType ?? typeof(object), options, $"{path}[]", found);
                }

                break;
            case JsonTypeInfoKind.Dictionary:
                if (schema.ActualProperties.Count > 0 || !schema.AllowAdditionalProperties)
                {
                    found.Add($"{path}: the type is a map of any names, and the schema does not leave the object open.");
                }
                else if (schema.AdditionalPropertiesSchema is { } values)
                {
                    Compare(values, info.ElementType ?? typeof(object), options, $"{path}{{}}", found);
                }

                break;
            case JsonTypeInfoKind.None when type.IsEnum:
                CompareValues(schema, type, options, path, found);
                break;
            case JsonTypeInfoKind.None when type == typeof(JsonElement) || typeof(JsonNode).IsAssignableFrom(type):
                if (schema.ActualProperties.Count > 0 || !schema.AllowAdditionalProperties)
                {
                    found.Add($"{path}: the type holds any JSON, and the schema does not leave it open.");
                }

                break;
            default:
                if (schema.ActualProperties.Count > 0)
                {
                    found.Add($"{path}: the type is a single value, and the schema defines properties.");
                }

                break;
        }
    }

    private static void CompareKinds(JsonSchema schema, JsonPolymorphismOptions polymorphism, JsonSerializerOptions options, string path, List<string> found)
    {
        string discriminator = polymorphism.TypeDiscriminatorPropertyName;
        Dictionary<string, JsonSchema> branches = [];
        foreach (JsonSchema branch in schema.OneOf.Select(one => one.ActualSchema))
        {
            if (KindOf(branch, discriminator) is { } kind)
            {
                branches[kind] = branch;
            }
            else
            {
                found.Add($"{path}: a kind in the schema does not name itself in '{discriminator}' with one value.");
            }
        }

        HashSet<string> kinds = [];
        foreach (JsonDerivedType derived in polymorphism.DerivedTypes)
        {
            string kind = derived.TypeDiscriminator?.ToString() ?? string.Empty;
            kinds.Add(kind);
            if (branches.TryGetValue(kind, out JsonSchema? branch))
            {
                CompareObject(branch, options.GetTypeInfo(derived.DerivedType), options, $"{path}({kind})", discriminator, found);
            }
            else
            {
                found.Add($"{path}: the type has the kind '{kind}', which the schema does not define.");
            }
        }

        found.AddRange(branches.Keys.Where(kind => !kinds.Contains(kind)).Select(kind => $"{path}: the schema defines the kind '{kind}', which the type does not read."));
    }

    private static string? KindOf(JsonSchema branch, string discriminator) =>
        branch.ActualProperties.TryGetValue(discriminator, out JsonSchemaProperty? property)
        && property.ActualSchema.Enumeration is { Count: 1 } values
        && values.Single() is string kind
            ? kind
            : null;

    private static void CompareObject(JsonSchema schema, JsonTypeInfo info, JsonSerializerOptions options, string path, string? discriminator, List<string> found)
    {
        if (info.Kind != JsonTypeInfoKind.Object)
        {
            found.Add($"{path}: the type is not an object.");
            return;
        }

        Dictionary<string, JsonPropertyInfo> written = info.Properties.Where(property => !property.IsExtensionData).ToDictionary(property => property.Name, StringComparer.Ordinal);
        Dictionary<string, JsonSchemaProperty> defined = schema.ActualProperties
            .Where(property => property.Key != discriminator)
            .ToDictionary(property => property.Key, property => property.Value, StringComparer.Ordinal);

        found.AddRange(written.Keys.Where(name => !defined.ContainsKey(name)).Select(name => $"{path}: the type writes '{name}', which the schema does not define."));
        found.AddRange(defined.Keys.Where(name => !written.ContainsKey(name)).Select(name => $"{path}: the schema defines '{name}', which the type does not read."));

        if (discriminator is not null && !schema.RequiredProperties.Contains(discriminator))
        {
            found.Add($"{path}: the schema does not require the kind '{discriminator}'.");
        }

        bool open = info.Properties.Any(property => property.IsExtensionData);
        if (open != schema.AllowAdditionalProperties)
        {
            found.Add(open
                ? $"{path}: the type keeps properties it does not define, and the schema refuses them."
                : $"{path}: the schema leaves the object open, and the type refuses properties it does not define.");
        }

        foreach ((string name, JsonPropertyInfo property) in written)
        {
            if (!defined.TryGetValue(name, out JsonSchemaProperty? definition))
            {
                continue;
            }

            bool requiredByType = property.IsRequired || property.AssociatedParameter is { HasDefaultValue: false };
            bool requiredBySchema = schema.RequiredProperties.Contains(name);
            if (requiredByType != requiredBySchema)
            {
                found.Add(requiredByType
                    ? $"{path}.{name}: the type requires it, and the schema does not."
                    : $"{path}.{name}: the schema requires it, and the type does not.");
            }

            Compare(definition, property.PropertyType, options, $"{path}.{name}", found);
        }
    }

    private static void CompareValues(JsonSchema schema, Type type, JsonSerializerOptions options, string path, List<string> found)
    {
        HashSet<string> written = [.. Enum.GetValues(type).Cast<object>().Select(value => JsonSerializer.SerializeToNode(value, type, options)!.GetValue<string>())];
        HashSet<string> defined = [.. schema.Enumeration.OfType<string>()];
        found.AddRange(written.Where(value => !defined.Contains(value)).Select(value => $"{path}: the type writes the value '{value}', which the schema does not define."));
        found.AddRange(defined.Where(value => !written.Contains(value)).Select(value => $"{path}: the schema defines the value '{value}', which the type does not read."));
    }
}
