using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Coanda.Core.Agents;

/// <summary>
/// The definition of an agent: a persona in five parts, a model spec, a tool set, skills and MCP
/// servers by name, and the one answer schema its agent steps end in, held as a name and as JSON
/// Schema content. An agent is data the daemon validates at load; the agents the binary ships and a
/// user's agent are the same shape.
/// </summary>
public sealed record AgentDefinition
{
    public required string Name { get; init; }
    public required AgentPersona Persona { get; init; }
    public required ModelSpec Model { get; init; }
    /// <summary>Names from <see cref="ToolCatalog"/>.</summary>
    public required IReadOnlyList<string> Tools { get; init; }
    /// <summary>Vendor-native skills, referenced by name.</summary>
    public required IReadOnlyList<string> Skills { get; init; }
    /// <summary>MCP servers by name; how one is launched is node-local and never in the store.</summary>
    public required IReadOnlyList<string> McpServers { get; init; }
    /// <summary>The name of the answer schema, which a flow's agent step names too.</summary>
    public required string AnswerSchema { get; init; }

    /// <summary>
    /// The answer schema as JSON Schema content, checked by <see cref="AnswerSchemaRules"/> when the
    /// agent is drafted or imported. Agent content stored before an agent carried it holds none and
    /// names one of the schemas in <see cref="NameOnlyAnswerSchemas"/>; it is left out of the JSON
    /// and of the hash when absent, so such content reads and hashes as it was stored.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? AnswerSchemaContent { get; init; }

    /// <summary>
    /// Content hash of the definition. The tool set, the skills, and the MCP servers are taken in
    /// ordinal order, so listing them in another order does not change it.
    /// </summary>
    [JsonIgnore]
    public string Hash => ComputeHash();

    public bool HoldsTool(string tool) => Tools.Contains(tool, StringComparer.Ordinal);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new DomainException("An agent needs a name.");
        }

        RequirePart(Persona?.Role, "a role", "persona");
        RequirePart(Persona?.Purpose, "a purpose", "persona");
        RequirePart(Persona?.Output, "an output", "persona");
        RequirePart(Persona?.Rules, "rules", "persona");
        RequirePart(Persona?.Prohibitions, "prohibitions", "persona");

        RequirePart(Model?.Vendor, "a vendor", "model spec");
        RequirePart(Model?.Model, "a model", "model spec");
        RequirePart(Model?.Effort, "an effort", "model spec");

        if (string.IsNullOrWhiteSpace(AnswerSchema))
        {
            throw new DomainException($"Agent '{Name}' needs the name of an answer schema.");
        }

        if (AnswerSchemaContent is not { } content)
        {
            throw new DomainException(
                $"Agent '{Name}' holds no answer schema content; an agent carries its answer schema '{AnswerSchema}' as JSON Schema content beside its name.");
        }

        AnswerSchemaRules.Verdicts(Name, content);

        foreach (string tool in Tools)
        {
            if (!ToolCatalog.Holds(tool))
            {
                throw new DomainException(
                    $"Agent '{Name}' names the tool '{tool}', which the shipped tool catalog does not hold; it holds {string.Join(", ", ToolCatalog.Names)}.");
            }
        }

        RequireNamed(Skills, "a skill");
        RequireNamed(McpServers, "an MCP server");
        RequireNoDuplicates(Tools, "tool", "tool set");
        RequireNoDuplicates(Skills, "skill", "skills");
        RequireNoDuplicates(McpServers, "MCP server", "MCP servers");
    }

    /// <summary>
    /// The answer schema this agent's steps validate against and its prompt shows: its own content,
    /// or, for agent content stored before an agent carried any, the schema in
    /// <see cref="NameOnlyAnswerSchemas"/> it names. Content that keeps neither is a rule broken.
    /// </summary>
    public AnswerSchema ResolveAnswerSchema()
    {
        if (AnswerSchemaContent is { } content)
        {
            IReadOnlyList<string> verdicts = AnswerSchemaRules.Verdicts(Name, content);
            return new AnswerSchema(AnswerSchema, verdicts, JsonSerializer.Serialize(content, ShownOptions));
        }

        return NameOnlyAnswerSchemas.Find(AnswerSchema)
            ?? throw new DomainException(
                $"Agent '{Name}' holds no answer schema content and names the answer schema '{AnswerSchema}', which is not one an agent stored without content could name; those are {string.Join(", ", NameOnlyAnswerSchemas.Names)}.");
    }

    private void RequirePart(string? value, string part, string held)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DomainException($"Agent '{Name}' needs {part} in its {held}.");
        }
    }

    private void RequireNamed(IReadOnlyList<string> names, string noun)
    {
        if (names.Any(string.IsNullOrWhiteSpace))
        {
            throw new DomainException($"Agent '{Name}' lists {noun} with a blank name.");
        }
    }

    private void RequireNoDuplicates(IReadOnlyList<string> names, string noun, string list)
    {
        string? twice = names.GroupBy(n => n, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1)?.Key;
        if (twice is not null)
        {
            throw new DomainException($"Agent '{Name}' lists the {noun} '{twice}' twice in its {list}.");
        }
    }

    private string ComputeHash()
    {
        var canonical = new
        {
            name = Name,
            persona = new
            {
                role = Persona.Role,
                purpose = Persona.Purpose,
                output = Persona.Output,
                rules = Persona.Rules,
                prohibitions = Persona.Prohibitions,
            },
            model = new { vendor = Model.Vendor, model = Model.Model, effort = Model.Effort },
            tools = Ordinal(Tools),
            skills = Ordinal(Skills),
            mcp = Ordinal(McpServers),
            answer = AnswerSchema,
        };

        // Content without schema content hashes exactly as it did before agents carried any.
        byte[] bytes;
        if (AnswerSchemaContent is { } content)
        {
            JsonNode node = JsonSerializer.SerializeToNode(canonical)
                ?? throw new InvalidOperationException("An agent's canonical form serialized to nothing.");
            node["answerSchema"] = Sorted(content);
            bytes = JsonSerializer.SerializeToUtf8Bytes(node);
        }
        else
        {
            bytes = JsonSerializer.SerializeToUtf8Bytes(canonical);
        }

        return Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
    }

    /// <summary>The content with every object's members in ordinal order, so their order does not change the hash.</summary>
    private static JsonNode? Sorted(JsonElement element) => element.ValueKind switch
    {
        // A member named twice keeps its last value, as the JSON Schema reader does; a draft refuses one.
        JsonValueKind.Object => new JsonObject(
            element.EnumerateObject()
                .GroupBy(member => member.Name, StringComparer.Ordinal)
                .OrderBy(named => named.Key, StringComparer.Ordinal)
                .Select(named => KeyValuePair.Create(named.Key, Sorted(named.Last().Value)))),
        JsonValueKind.Array => new JsonArray([.. element.EnumerateArray().Select(Sorted)]),
        _ => JsonValue.Create(element),
    };

    private static string[] Ordinal(IReadOnlyList<string> names) => [.. names.Order(StringComparer.Ordinal)];

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    // The schema document is shown to the agent in its prompt, never embedded in HTML, so characters
    // such as '+' and '<' in a pattern or a description are written as themselves.
    private static readonly JsonSerializerOptions ShownOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly JsonSerializerOptions ReadOptions = new() { RespectNullableAnnotations = true };

    public static string CanonicalJson(AgentDefinition agent) => JsonSerializer.Serialize(agent, WriteOptions);

    /// <summary>Reads an agent from the JSON <see cref="CanonicalJson"/> writes; anything else is a rule broken.</summary>
    public static AgentDefinition FromJson(string content)
    {
        try
        {
            return JsonSerializer.Deserialize<AgentDefinition>(content, ReadOptions)
                ?? throw new DomainException("The content is empty; it cannot be read as an agent definition.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new DomainException($"The content cannot be read as an agent definition: {exception.Message}");
        }
    }

    public override string ToString() => $"{Name}@{Hash}";
}
