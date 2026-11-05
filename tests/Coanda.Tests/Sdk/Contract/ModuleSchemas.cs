using System.Text.Json.Nodes;
using NJsonSchema;

namespace Coanda.Tests.Sdk.Contract;

/// <summary>
/// The module API's schema folder and its index, and the documents they address. An address is a
/// file in the folder and, where the file holds more than one document, a JSON pointer to a
/// definition in it, as in <c>dispatch.schema.json#/definitions/request</c>.
/// </summary>
public static class ModuleSchemas
{
    public const string Folder = "schemas/module/v1";

    public const string IndexFile = "index.json";

    private const string DefinitionPointer = "/definitions/";

    private static readonly Dictionary<string, JsonSchema> CompiledFiles = new(StringComparer.Ordinal);

    public static string FolderPath => RepositoryFiles.PathOf(Folder);

    /// <summary>Every schema file in the folder, by file name, without the index.</summary>
    public static IReadOnlyList<string> SchemaFiles { get; } =
        [.. Directory.GetFiles(FolderPath, "*.schema.json").Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];

    public static JsonObject Index { get; } = ReadObject(IndexFile);

    /// <summary>Each call the index lists, with its request and answer addresses, null where it has none.</summary>
    public static IReadOnlyList<(string Call, string? Request, string? Answer)> Calls { get; } =
        [.. Index["calls"]!.AsArray().Select(call => (
            (string)call!["call"]!,
            (string?)call["request"],
            (string?)call["answer"]))];

    /// <summary>Every address the index names, a call's request or answer or one of its other documents.</summary>
    public static IReadOnlyList<string> Addresses { get; } =
    [
        .. Calls.SelectMany(call => new[] { call.Request, call.Answer }).OfType<string>(),
        .. Index["documents"]!.AsArray().Select(document => (string)document!["address"]!),
    ];

    public static JsonObject ReadObject(string file) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(FolderPath, file)))!.AsObject();

    public static (string File, string? Pointer) Split(string address) =>
        address.Split('#', 2) is [string file, string pointer]
            ? (file, pointer)
            : (address, null);

    /// <summary>Whether the address names a file in the folder and, when it has a pointer, a definition the file holds.</summary>
    public static bool Exists(string address)
    {
        (string file, string? pointer) = Split(address);
        if (!File.Exists(Path.Combine(FolderPath, file)))
        {
            return false;
        }

        return pointer is null
            || (DefinitionName(pointer) is { } name && ReadObject(file)["definitions"]?[name] is JsonObject);
    }

    /// <summary>The compiled document the address names.</summary>
    public static JsonSchema Load(string address)
    {
        (string file, string? pointer) = Split(address);
        JsonSchema root = Compiled(file);
        if (pointer is null)
        {
            return root;
        }

        string name = DefinitionName(pointer) ?? throw new InvalidOperationException($"{address} does not point at a definition.");
        return root.Definitions[name];
    }

    private static string? DefinitionName(string pointer) =>
        pointer.StartsWith(DefinitionPointer, StringComparison.Ordinal) && pointer.Length > DefinitionPointer.Length && !pointer[DefinitionPointer.Length..].Contains('/')
            ? pointer[DefinitionPointer.Length..]
            : null;

    private static JsonSchema Compiled(string file)
    {
        lock (CompiledFiles)
        {
            if (!CompiledFiles.TryGetValue(file, out JsonSchema? schema))
            {
                // The text is in memory, so this completes synchronously; there is nothing to cancel.
                schema = JsonSchema.FromJsonAsync(File.ReadAllText(Path.Combine(FolderPath, file))).GetAwaiter().GetResult();
                CompiledFiles[file] = schema;
            }

            return schema;
        }
    }
}
