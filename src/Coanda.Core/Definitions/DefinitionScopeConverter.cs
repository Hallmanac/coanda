using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coanda.Core.Definitions;

/// <summary>Writes a scope by its name, and reads the earlier name <c>Shipped</c> as <see cref="DefinitionScope.Module"/>.</summary>
public sealed class DefinitionScopeConverter : JsonConverter<DefinitionScope>
{
    /// <summary>The name shipped scope was stored under before module scope took its place.</summary>
    public const string EarlierModuleName = "Shipped";

    public override DefinitionScope Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? name = reader.TokenType == JsonTokenType.String
            ? reader.GetString()
            : throw new JsonException($"A definition scope is stored as its name; a {reader.TokenType} was found.");
        return name switch
        {
            nameof(DefinitionScope.Project) => DefinitionScope.Project,
            nameof(DefinitionScope.Owner) => DefinitionScope.Owner,
            nameof(DefinitionScope.Module) or EarlierModuleName => DefinitionScope.Module,
            _ => throw new JsonException($"'{name}' is not a definition scope; a scope is Project, Owner, or Module."),
        };
    }

    public override void Write(Utf8JsonWriter writer, DefinitionScope value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
