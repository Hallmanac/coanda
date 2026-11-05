using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Sdk.Contract;
using NJsonSchema;
using NJsonSchema.Validation;

namespace Coanda.Daemon.Modules;

/// <summary>
/// What a module's directory brings the store: the module's name and version, from its
/// <c>module.json</c>, and the definitions in its definitions folder, each with the file it was read
/// from. The folder holds a <c>types</c>, a <c>flows</c>, and an <c>agents</c> folder, any of which may
/// be absent, each holding one JSON file per definition named for it; an entry whose name starts with a
/// dot is passed over. Install will read the same
/// layout from the module's artifact; until it exists the daemon reads it at start.
/// </summary>
public sealed record ModuleArtifact(string Module, string Version, IReadOnlyList<ModuleArtifactDefinition> Definitions)
{
    public const string ManifestFile = "module.json";

    public const string DefinitionsFolder = "definitions";

    /// <summary>The folder each kind of definition is read from, in the order they are imported.</summary>
    public static IReadOnlyList<(string Folder, DefinitionKind Kind)> KindFolders { get; } =
    [
        ("types", DefinitionKind.ParcelType),
        ("flows", DefinitionKind.Flow),
        ("agents", DefinitionKind.Agent),
    ];

    private static readonly Lazy<Task<JsonSchema>> ManifestSchema = new(ReadManifestSchemaAsync);

    /// <summary>A module's definitions given directly rather than read from files, as a test brings them.</summary>
    public static ModuleArtifact Of(string module, string version, IEnumerable<ModuleDefinition> definitions) =>
        new(module, version, [.. definitions.Select(d => new ModuleArtifactDefinition(d, null))]);

    /// <summary>
    /// Reads the module in <paramref name="directory"/>: its manifest, checked against the module API's
    /// manifest schema, and every definition file, each read through its kind's canonical read. Refuses,
    /// naming the path, a missing manifest or definitions folder, a manifest the schema refuses, an entry
    /// of the folder that is not a definition file, a file its kind's read refuses, a file not named for
    /// the definition it holds, and two files holding one kind and name.
    /// </summary>
    public static async Task<ModuleArtifact> ReadAsync(string directory, CancellationToken cancellationToken)
    {
        string manifestPath = Path.Combine(directory, ManifestFile);
        if (!File.Exists(manifestPath))
        {
            throw new DomainException(
                $"No module manifest at '{manifestPath}'; a module's directory holds its {ManifestFile} beside its {DefinitionsFolder} folder.");
        }

        ModuleManifest manifest = await ReadManifestAsync(manifestPath, cancellationToken);
        string definitionsPath = Path.Combine(directory, DefinitionsFolder);
        if (!Directory.Exists(definitionsPath))
        {
            throw new DomainException(
                $"No definitions folder at '{definitionsPath}'; module '{manifest.Name}' keeps its types, flows, and agents there, even when it brings none.");
        }

        RefuseStrayEntries(definitionsPath);
        List<ModuleArtifactDefinition> definitions = [];
        foreach ((string folder, DefinitionKind kind) in KindFolders)
        {
            string kindPath = Path.Combine(definitionsPath, folder);
            if (!Directory.Exists(kindPath))
            {
                continue;
            }

            if (Visible(Directory.EnumerateDirectories(kindPath)).FirstOrDefault() is { } nested)
            {
                throw NotADefinitionFile(nested);
            }

            foreach (string file in Visible(Directory.EnumerateFiles(kindPath)).Order(StringComparer.Ordinal))
            {
                definitions.Add(new ModuleArtifactDefinition(
                    await ReadDefinitionAsync(manifest, kind, file, cancellationToken), file));
            }
        }

        var artifact = new ModuleArtifact(manifest.Name, manifest.Version, definitions);
        artifact.RequireOnePerName();
        return artifact;
    }

    /// <summary>Refuses two definitions of one kind and name, which would otherwise import as two versions of it at once.</summary>
    public void RequireOnePerName()
    {
        if (Definitions
                .GroupBy(d => (d.Definition.Kind, Name: d.Definition.Name.ToLowerInvariant()))
                .FirstOrDefault(g => g.Count() > 1) is { } twice)
        {
            throw new DomainException(
                $"Module '{Module}' version {Version} brings '{twice.First().Definition.Name}' more than once{FilesOf(twice)}; a module brings one definition of a kind and name.");
        }
    }

    /// <summary>A refusal of <paramref name="definition"/>'s import, naming the file it came from when it came from one.</summary>
    public DomainException Refusal(ModuleArtifactDefinition definition, DomainException broken) =>
        new(definition.File is null
            ? $"Module '{Module}' version {Version} cannot be imported: its {definition.Definition.Kind} '{definition.Definition.Name}' breaks a rule: {broken.Message}"
            : $"Module '{Module}' version {Version} cannot be imported: the definition file '{definition.File}' breaks a rule: {broken.Message}");

    private static async Task<ModuleManifest> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        string text = await File.ReadAllTextAsync(path, cancellationToken);
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            throw new DomainException($"The module manifest at '{path}' is not JSON: {exception.Message}");
        }

        ICollection<ValidationError> failures = (await ManifestSchema.Value).Validate(text);
        if (failures.Count > 0)
        {
            throw new DomainException(
                $"The module manifest at '{path}' breaks the module API's manifest schema: {string.Join("; ", failures.Select(f => $"{f.Kind} at {f.Path ?? "#"}"))}.");
        }

        try
        {
            return JsonSerializer.Deserialize<ModuleManifest>(text, ModuleJson.Options)
                ?? throw new DomainException($"The module manifest at '{path}' is empty.");
        }
        catch (JsonException exception)
        {
            throw new DomainException($"The module manifest at '{path}' cannot be read: {exception.Message}");
        }
    }

    private static async Task<ModuleDefinition> ReadDefinitionAsync(
        ModuleManifest manifest, DefinitionKind kind, string file, CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetExtension(file), ".json", StringComparison.Ordinal))
        {
            throw NotADefinitionFile(file);
        }

        ModuleDefinition definition;
        try
        {
            definition = ModuleDefinition.Read(kind, await File.ReadAllTextAsync(file, cancellationToken));
        }
        catch (DomainException broken)
        {
            throw new DomainException(
                $"Module '{manifest.Name}' version {manifest.Version} cannot be imported: the definition file '{file}' breaks a rule: {broken.Message}");
        }

        if (!string.Equals(Path.GetFileNameWithoutExtension(file), definition.Name, StringComparison.Ordinal))
        {
            throw new DomainException(
                $"Module '{manifest.Name}' version {manifest.Version} cannot be imported: the definition file '{file}' holds '{definition.Name}'; a definition file is named for the definition it holds, '{definition.Name}.json'.");
        }

        return definition;
    }

    private static void RefuseStrayEntries(string definitionsPath)
    {
        HashSet<string> folders = [.. KindFolders.Select(k => k.Folder)];
        if (Visible(Directory.EnumerateFiles(definitionsPath)).FirstOrDefault() is { } file)
        {
            throw NotADefinitionFile(file);
        }

        if (Visible(Directory.EnumerateDirectories(definitionsPath)).FirstOrDefault(d => !folders.Contains(Path.GetFileName(d))) is { } folder)
        {
            throw NotADefinitionFile(folder);
        }
    }

    /// <summary>The entries whose names do not start with a dot, so a file a file manager leaves behind, such as <c>.DS_Store</c>, is passed over.</summary>
    private static IEnumerable<string> Visible(IEnumerable<string> entries) =>
        entries.Where(entry => !Path.GetFileName(entry).StartsWith('.'));

    private static DomainException NotADefinitionFile(string path) =>
        new($"'{path}' is not a definition file; a module's {DefinitionsFolder} folder holds a types, a flows, and an agents folder of JSON files, one definition per file.");

    private static string FilesOf(IEnumerable<ModuleArtifactDefinition> definitions)
    {
        string[] files = [.. definitions.Select(d => d.File).OfType<string>()];
        return files.Length == 0 ? string.Empty : $", in {string.Join(" and ", files.Select(f => $"'{f}'"))}";
    }

    private static async Task<JsonSchema> ReadManifestSchemaAsync()
    {
        await using Stream stream = typeof(ModuleArtifact).Assembly.GetManifestResourceStream(ManifestSchemaResource)
            ?? throw new InvalidOperationException($"The daemon carries no '{ManifestSchemaResource}'.");
        using var reader = new StreamReader(stream);
        return await JsonSchema.FromJsonAsync(await reader.ReadToEndAsync());
    }

    /// <summary>The module API's manifest schema, which the daemon's project embeds from <c>schemas/module/v1/</c>.</summary>
    private const string ManifestSchemaResource = "Coanda.Daemon.Modules.manifest.schema.json";
}
