using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Coanda.Sdk.Contract;
using NJsonSchema;

namespace Coanda.Tests.Sdk.Contract;

/// <summary>
/// The SDK's contract types hold to the module API's schemas in both directions: each type's
/// shape is the schema's shape, and an example of every document and of every kind of an answer,
/// carrying every property the schema closes, validates, reads into its type, and writes back as
/// the same JSON value, which the written JSON validates as well.
/// </summary>
public sealed class ContractTypeTests
{
    private static readonly JsonSerializerOptions Options = ModuleJson.Options;

    public static TheoryData<string> Documents() => [.. ContractDocuments.Types.Keys];

    [Fact]
    public void Every_document_the_index_names_has_one_contract_type()
    {
        Assert.Equal(
            ModuleSchemas.Addresses.Order(StringComparer.Ordinal),
            ContractDocuments.Types.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(ContractDocuments.Types.Count, ContractDocuments.Types.Values.Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void The_contract_type_has_the_shape_of_its_document(string address)
    {
        Assert.Empty(ContractShape.Disagreements(ModuleSchemas.Load(address), ContractDocuments.Types[address], Options));
    }

    [Theory]
    [MemberData(nameof(ContractDocuments.ExampleNames), MemberType = typeof(ContractDocuments))]
    public void The_example_validates_reads_into_its_type_and_writes_back_the_same(string name)
    {
        (string address, Type type, string json) = ContractDocuments.Example(name);
        JsonSchema schema = ModuleSchemas.Load(address);
        Assert.Empty(schema.Validate(json));

        object value = JsonSerializer.Deserialize(json, type, Options) ?? throw new InvalidOperationException($"The {name} example read as null.");
        string written = JsonSerializer.Serialize(value, type, Options);

        Assert.Empty(schema.Validate(written));
        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(written)),
            $"The {name} example wrote back as {written}");
    }

    [Theory]
    [MemberData(nameof(ContractDocuments.ExampleNames), MemberType = typeof(ContractDocuments))]
    public void The_example_carries_every_property_at_every_level_the_schema_closes(string name)
    {
        (string address, _, string json) = ContractDocuments.Example(name);
        var missing = new List<string>();
        Carries(ModuleSchemas.Load(address), JsonNode.Parse(json), "#", missing);
        Assert.Empty(missing);
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void Every_kind_of_a_document_has_an_example(string address)
    {
        JsonSchema schema = ModuleSchemas.Load(address).ActualSchema;
        string[] examples = [.. ContractDocuments.Examples.Where(example => example.Address == address).Select(example => example.Json)];
        Assert.NotEmpty(examples);
        foreach (JsonSchema kind in schema.OneOf)
        {
            Assert.Contains(examples, json => kind.ActualSchema.Validate(json).Count == 0);
        }
    }

    [Fact]
    public void A_property_the_type_writes_and_the_schema_lacks_is_a_disagreement()
    {
        JsonSchema schema = Compile("""
            { "type": "object", "additionalProperties": false, "required": ["name"], "properties": { "name": { "type": "string" } } }
            """);
        Assert.Equal(["#: the type writes 'extra', which the schema does not define."], ContractShape.Disagreements(schema, typeof(WritesMore), Options));
        Assert.NotEmpty(schema.Validate(JsonSerializer.Serialize(new WritesMore("a", "b"), Options)));
    }

    [Fact]
    public void A_property_the_schema_defines_and_the_type_lacks_is_a_disagreement()
    {
        JsonSchema schema = Compile("""
            { "type": "object", "additionalProperties": false, "required": ["name", "extra"], "properties": { "name": { "type": "string" }, "extra": { "type": "string" } } }
            """);
        Assert.Equal(["#: the schema defines 'extra', which the type does not read."], ContractShape.Disagreements(schema, typeof(ReadsLess), Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ReadsLess>("""{ "name": "a", "extra": "b" }""", Options));
    }

    [Fact]
    public void A_kind_on_only_one_side_is_a_disagreement()
    {
        JsonSchema schema = Compile("""
            {
              "oneOf": [
                { "type": "object", "additionalProperties": false, "required": ["answer"], "properties": { "answer": { "enum": ["one"] } } },
                { "type": "object", "additionalProperties": false, "required": ["answer"], "properties": { "answer": { "enum": ["three"] } } }
              ]
            }
            """);
        Assert.Equal(
            [
                "#: the type has the kind 'two', which the schema does not define.",
                "#: the schema defines the kind 'three', which the type does not read.",
            ],
            ContractShape.Disagreements(schema, typeof(Kinds), Options));
    }

    [Fact]
    public void An_object_open_on_only_one_side_is_a_disagreement()
    {
        JsonSchema open = Compile("""
            { "type": "object", "required": ["name"], "properties": { "name": { "type": "string" } } }
            """);
        Assert.Equal(
            ["#: the schema leaves the object open, and the type refuses properties it does not define."],
            ContractShape.Disagreements(open, typeof(ReadsLess), Options));
    }

    [Fact]
    public void A_property_required_on_only_one_side_is_a_disagreement()
    {
        JsonSchema schema = Compile("""
            { "type": "object", "additionalProperties": false, "properties": { "name": { "type": "string" } } }
            """);
        Assert.Equal(["#.name: the type requires it, and the schema does not."], ContractShape.Disagreements(schema, typeof(ReadsLess), Options));
    }

    private static JsonSchema Compile(string json) =>
        // The text is in memory, so this completes synchronously; there is nothing to cancel.
        JsonSchema.FromJsonAsync(json).GetAwaiter().GetResult();

    /// <summary>
    /// Lists every property a closed object of <paramref name="schema"/> defines that
    /// <paramref name="node"/> leaves out, and every list it leaves empty, recursing into the kind
    /// of a several-kind document that the node is.
    /// </summary>
    private static void Carries(JsonSchema schema, JsonNode? node, string path, List<string> missing)
    {
        schema = schema.ActualSchema;
        if (schema.OneOf.Count > 0)
        {
            string text = node?.ToJsonString() ?? "null";
            JsonSchema? kind = schema.OneOf.Select(one => one.ActualSchema).FirstOrDefault(one => one.Validate(text).Count == 0);
            if (kind is null)
            {
                missing.Add($"{path}: the example is none of the schema's kinds.");
                return;
            }

            schema = kind;
        }

        switch (node)
        {
            case JsonObject value when !schema.AllowAdditionalProperties:
                foreach ((string name, JsonSchemaProperty property) in schema.ActualProperties)
                {
                    if (value[name] is { } child)
                    {
                        Carries(property, child, $"{path}.{name}", missing);
                    }
                    else
                    {
                        missing.Add($"{path}.{name}");
                    }
                }

                break;
            case JsonArray items when schema.Item is { } item:
                if (items.Count == 0)
                {
                    missing.Add($"{path}[]: the list is empty.");
                }

                for (int i = 0; i < items.Count; i++)
                {
                    Carries(item, items[i], $"{path}[{i}]", missing);
                }

                break;
        }
    }

    private sealed record WritesMore(string Name, string Extra);

    private sealed record ReadsLess(string Name);

    [JsonPolymorphic(TypeDiscriminatorPropertyName = "answer")]
    [JsonDerivedType(typeof(One), "one")]
    [JsonDerivedType(typeof(Two), "two")]
    private abstract record Kinds;

    private sealed record One : Kinds;

    private sealed record Two : Kinds;
}

