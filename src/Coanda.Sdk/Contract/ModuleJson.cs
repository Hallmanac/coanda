using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Coanda.Sdk.Contract;

/// <summary>
/// How the contract types are written and read as the module API's JSON. Property names are camel
/// case, an absent optional property is left out rather than written as null, and reading refuses
/// what the schemas refuse: a property the type does not define, a required property missing, and
/// a null where the type allows none. An answer's kind may come anywhere among its properties.
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
            TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) },
        };
        options.MakeReadOnly();
        return options;
    }
}
