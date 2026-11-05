using System.Text.Json.Nodes;

namespace Coanda.Tests.Sdk.Contract;

/// <summary>
/// The module API's schema folder: one draft-07 file per document group, each naming module API
/// version 1 in its <c>$id</c>, an index whose every address exists, and every object closed but the
/// few the design leaves open.
/// </summary>
public sealed class ModuleSchemaTests
{
    /// <summary>The objects the design leaves open, each as its file and the JSON pointer to it.</summary>
    private static readonly string[] OpenObjects =
    [
        "describe.schema.json#/definitions/schema",
        "dispatch.schema.json#/definitions/request/properties/arguments",
        "dispatch.schema.json#/definitions/result",
        "recorded-event.schema.json#/properties/fields",
        "status.schema.json#/definitions/result",
    ];

    public static TheoryData<string> Files() => [.. ModuleSchemas.SchemaFiles];

    [Fact]
    public void The_folder_holds_one_schema_file_for_each_document_group()
    {
        Assert.Equal(
            [
                "approval.schema.json",
                "describe.schema.json",
                "dispatch.schema.json",
                "feed-delivery.schema.json",
                "manifest.schema.json",
                "recorded-event.schema.json",
                "status.schema.json",
                "stop.schema.json",
                "watch-request.schema.json",
            ],
            ModuleSchemas.SchemaFiles);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void Each_file_is_draft_07_and_its_id_names_module_api_version_1(string file)
    {
        JsonObject schema = ModuleSchemas.ReadObject(file);
        Assert.Equal("http://json-schema.org/draft-07/schema#", (string?)schema["$schema"]);
        Assert.Equal($"urn:coanda:module-api:1:{file[..^".schema.json".Length]}", (string?)schema["$id"]);
    }

    [Fact]
    public void The_index_lists_the_calls_a_module_answers_with_their_documents()
    {
        Assert.Equal(1, (int?)ModuleSchemas.Index["moduleApiVersion"]);
        Assert.Equal(
            [
                ("health", null, null),
                ("describe", null, "describe.schema.json"),
                ("dispatch", "dispatch.schema.json#/definitions/request", "dispatch.schema.json#/definitions/answer"),
                ("status", "status.schema.json#/definitions/request", "status.schema.json#/definitions/answer"),
                ("stop", "stop.schema.json", null),
                ("feed", "feed-delivery.schema.json", null),
                ("watch", "watch-request.schema.json", null),
            ],
            ModuleSchemas.Calls);
    }

    [Fact]
    public void Every_address_the_index_names_exists()
    {
        Assert.All(ModuleSchemas.Addresses, address => Assert.True(ModuleSchemas.Exists(address), $"The index names {address}, which does not exist."));
    }

    [Theory]
    [InlineData("missing.schema.json")]
    [InlineData("dispatch.schema.json#/definitions/missing")]
    [InlineData("dispatch.schema.json#/properties/request")]
    [InlineData("dispatch.schema.json#/definitions/request/properties")]
    public void An_address_that_names_no_file_or_no_definition_of_it_does_not_exist(string address)
    {
        Assert.False(ModuleSchemas.Exists(address));
    }

    [Fact]
    public void Every_schema_file_is_named_by_the_index()
    {
        HashSet<string> named = [.. ModuleSchemas.Addresses.Select(address => ModuleSchemas.Split(address).File)];
        Assert.All(ModuleSchemas.SchemaFiles, file => Assert.Contains(file, named));
    }

    [Fact]
    public void Every_object_refuses_properties_it_does_not_define_except_the_open_ones()
    {
        List<string> open = [];
        foreach (string file in ModuleSchemas.SchemaFiles)
        {
            Collect(ModuleSchemas.ReadObject(file), $"{file}#", open);
        }

        Assert.Equal(OpenObjects, open.Order(StringComparer.Ordinal));
    }

    /// <summary>Adds the pointer of every object schema under <paramref name="node"/> that does not set <c>additionalProperties</c> to false.</summary>
    private static void Collect(JsonObject node, string pointer, List<string> open)
    {
        bool isObjectSchema = (string?)node["type"] == "object" || node["properties"] is not null;
        bool closed = node["additionalProperties"] is JsonValue flag && flag.TryGetValue(out bool allowed) && !allowed;
        if (isObjectSchema && !closed)
        {
            open.Add(pointer);
        }

        foreach ((string key, JsonNode? child) in node)
        {
            switch (key, child)
            {
                case ("properties" or "definitions", JsonObject members):
                    foreach ((string name, JsonNode? member) in members)
                    {
                        if (member is JsonObject schema)
                        {
                            Collect(schema, $"{pointer}/{key}/{name}", open);
                        }
                    }

                    break;
                case ("items" or "additionalProperties", JsonObject schema):
                    Collect(schema, $"{pointer}/{key}", open);
                    break;
                case ("oneOf", JsonArray branches):
                    for (int i = 0; i < branches.Count; i++)
                    {
                        if (branches[i] is JsonObject branch)
                        {
                            Collect(branch, $"{pointer}/oneOf/{i}", open);
                        }
                    }

                    break;
            }
        }
    }
}
