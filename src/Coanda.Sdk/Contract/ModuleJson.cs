using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Coanda.Sdk.Contract;

/// <summary>
/// How the contract types are written and read as the module API's JSON. Property names are camel
/// case, an absent optional property is left out rather than written as null, and reading refuses
/// what the schemas refuse: a property the type does not define, a required property missing, and
/// a null where the type allows none, whether that is a property's value or an element of a list
/// or a map whose element type allows none. An answer's kind may come anywhere among its properties.
/// </summary>
public static class ModuleJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectNullableAnnotations = true,
            RespectRequiredConstructorParameters = true,
            AllowOutOfOrderMetadataProperties = true,
            TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { RefuseNullElements } },
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) },
        };
        options.MakeReadOnly();
        return options;
    }

    /// <summary>
    /// Makes reading an object refuse a null element in any of its lists or maps whose element type
    /// is a reference type annotated as non-nullable. <see cref="JsonSerializerOptions.RespectNullableAnnotations"/>
    /// covers only properties and constructor parameters, never what a collection holds.
    /// </summary>
    private static void RefuseNullElements(JsonTypeInfo typeInfo)
    {
        if (typeInfo.Kind != JsonTypeInfoKind.Object)
        {
            return;
        }

        var nullability = new NullabilityInfoContext();
        List<(string Name, Func<object, object?> Get)> checkedProperties = [];
        foreach (JsonPropertyInfo property in typeInfo.Properties)
        {
            if (property.Get is { } get && property.AttributeProvider is PropertyInfo member && HoldsNonNullableElements(nullability, member))
            {
                checkedProperties.Add((property.Name, get));
            }
        }

        if (checkedProperties.Count == 0)
        {
            return;
        }

        Action<object>? earlier = typeInfo.OnDeserialized;
        typeInfo.OnDeserialized = value =>
        {
            earlier?.Invoke(value);
            foreach ((string name, Func<object, object?> get) in checkedProperties)
            {
                if (HoldsNull(get(value)))
                {
                    throw new JsonException($"'{name}' holds a null, which the module API does not allow there.");
                }
            }
        };
    }

    private static bool HoldsNonNullableElements(NullabilityInfoContext nullability, PropertyInfo member)
    {
        Type type = member.PropertyType;
        if (!type.IsGenericType)
        {
            return false;
        }

        NullabilityInfo info = nullability.Create(member);
        int element = type.GetGenericTypeDefinition() switch
        {
            Type definition when definition == typeof(IReadOnlyDictionary<,>) || definition == typeof(IDictionary<,>) || definition == typeof(Dictionary<,>) => 1,
            Type definition when definition == typeof(IReadOnlyList<>) || definition == typeof(IList<>) || definition == typeof(List<>)
                || definition == typeof(IReadOnlyCollection<>) || definition == typeof(ICollection<>) || definition == typeof(IEnumerable<>) => 0,
            _ => -1,
        };
        return element >= 0
            && !type.GenericTypeArguments[element].IsValueType
            && info.GenericTypeArguments[element].ReadState == NullabilityState.NotNull;
    }

    private static bool HoldsNull(object? collection) => collection switch
    {
        IDictionary map => map.Values.Cast<object?>().Any(value => value is null),
        IEnumerable items => items.Cast<object?>().Any(item => item is null),
        _ => false,
    };
}
