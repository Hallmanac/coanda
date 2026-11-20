using System.Text.Json;
using System.Text.RegularExpressions;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Sdk.Contract;
using NJsonSchema;
using NJsonSchema.Validation;

namespace Coanda.Daemon.Modules;

/// <summary>
/// What a module's directory brings the store: the module's name and version, from its
/// <c>module.json</c>, and the definitions in its definitions folder, each with the file it was read
/// from. The folder holds a <c>types</c>, a <c>flows</c>, an <c>agents</c>, and a <c>triggers</c> folder,
/// any of which may be absent, each holding one JSON file per definition named for it; an entry whose
/// name starts with a dot is passed over. Beside the definitions folder the directory may hold a
/// <c>skills</c> folder, one folder per skill named for the skill and holding a <c>SKILL.md</c>, which
/// an agent step whose agent names the skill gives its session (<see cref="Skills"/>). Install will
/// read the same layout from the module's artifact; until it exists the daemon reads it at start.
/// </summary>
public sealed partial record ModuleArtifact(string Module, string Version, IReadOnlyList<ModuleArtifactDefinition> Definitions)
{
    public const string ManifestFile = "module.json";

    public const string DefinitionsFolder = "definitions";

    /// <summary>The folder beside the definitions folder that holds the module's skills, one folder per skill.</summary>
    public const string SkillsFolder = "skills";

    /// <summary>The file every skill's folder holds, which a session is told to read before work the skill covers.</summary>
    public const string SkillFile = "SKILL.md";

    /// <summary>The skills the module's skills folder ships, in ordinal order of name; none when it has no skills folder.</summary>
    public IReadOnlyList<ModuleSkill> Skills { get; init; } = [];

    /// <summary>The folder each kind of definition is read from, in the order they are imported.</summary>
    public static IReadOnlyList<(string Folder, DefinitionKind Kind)> KindFolders { get; } =
    [
        ("types", DefinitionKind.ParcelType),
        ("flows", DefinitionKind.Flow),
        ("agents", DefinitionKind.Agent),
        ("triggers", DefinitionKind.Trigger),
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
    /// the definition it holds, and two files holding one kind and name. Reads the skills folder too,
    /// when there is one, and refuses what <see cref="ReadSkills"/> refuses.
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
                $"No definitions folder at '{definitionsPath}'; module '{manifest.Name}' keeps its types, flows, agents, and triggers there, even when it brings none.");
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

        var artifact = new ModuleArtifact(manifest.Name, manifest.Version, definitions)
        {
            Skills = ReadSkills(Path.Combine(directory, SkillsFolder)),
        };
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
            ? $"Module '{Module}' version {Version} cannot be imported: its {DefinitionDecider.Describe(definition.Definition.Kind)} '{definition.Definition.Name}' breaks a rule: {broken.Message}"
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

        ICollection<ValidationError> failures = (await ManifestSchema.Value.WaitAsync(cancellationToken)).Validate(text);
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

    /// <summary>
    /// The skills in the skills folder at <paramref name="skillsPath"/>, or none when there is no such
    /// folder. Refuses, naming the path, a skills folder that is, or holds anywhere inside it, a
    /// symbolic link or other reparse point, and, among the entries whose names do not start with a
    /// dot, a file directly in it, a folder whose name is not a plain name (ASCII letters, digits,
    /// hyphens, and underscores), and a folder holding no <c>SKILL.md</c>. A skill is only ever held as
    /// the folder read here, so a name an agent gives is looked up among these and never joined onto a path.
    /// </summary>
    public static IReadOnlyList<ModuleSkill> ReadSkills(string skillsPath)
    {
        var folder = new DirectoryInfo(skillsPath);
        if (FileEntries.IsLink(folder))
        {
            throw NotASkillsFolder(skillsPath, "is a symbolic link or other reparse point");
        }

        if (!folder.Exists)
        {
            return [];
        }

        RefuseLinksWithin(folder);
        List<ModuleSkill> skills = [];
        foreach (FileSystemInfo entry in folder.EnumerateFileSystemInfos("*", FileEntries.Everything).Where(e => !e.Name.StartsWith('.')).OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            if (entry is not DirectoryInfo skill)
            {
                throw NotASkillsFolder(entry.FullName, "is a file directly in a skills folder, which holds only one folder per skill");
            }

            if (!PlainName().IsMatch(skill.Name))
            {
                throw NotASkillsFolder(entry.FullName, "is a skill folder whose name is not a plain name (ASCII letters, digits, hyphens, and underscores)");
            }

            if (!skill.EnumerateFiles("*", FileEntries.Everything).Any(f => string.Equals(f.Name, SkillFile, StringComparison.Ordinal)))
            {
                throw NotASkillsFolder(entry.FullName, $"is a skill folder that holds no {SkillFile}");
            }

            skills.Add(new ModuleSkill(skill.Name, skill.FullName));
        }

        return skills;
    }

    private static void RefuseLinksWithin(DirectoryInfo directory)
    {
        foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos("*", FileEntries.Everything))
        {
            if (FileEntries.IsLink(entry))
            {
                throw NotASkillsFolder(entry.FullName, "is a symbolic link or other reparse point inside a skills folder");
            }

            if (entry is DirectoryInfo inner)
            {
                RefuseLinksWithin(inner);
            }
        }
    }

    private static DomainException NotASkillsFolder(string path, string what) =>
        new($"'{path}' {what}; a module's {SkillsFolder} folder holds one folder per skill, named for the skill with a plain name and holding a {SkillFile}, and no link anywhere inside it.");

    [GeneratedRegex(@"\A[A-Za-z0-9_-]+\z")]
    private static partial Regex PlainName();

    /// <summary>The entries whose names do not start with a dot, so a file a file manager leaves behind, such as <c>.DS_Store</c>, is passed over.</summary>
    private static IEnumerable<string> Visible(IEnumerable<string> entries) =>
        entries.Where(entry => !Path.GetFileName(entry).StartsWith('.'));

    private static DomainException NotADefinitionFile(string path) =>
        new($"'{path}' is not a definition file; a module's {DefinitionsFolder} folder holds a types, a flows, an agents, and a triggers folder of JSON files, one definition per file.");

    private static string FilesOf(IEnumerable<ModuleArtifactDefinition> definitions)
    {
        string[] files = [.. definitions.Select(d => d.File).OfType<string>()];
        return files.Length == 0 ? string.Empty : $", in {string.Join(" and ", files.Select(f => $"'{f}'"))}";
    }

    /// <summary>
    /// Reads the embedded manifest schema once for the whole process. It takes no cancellation token on
    /// purpose: <see cref="ManifestSchema"/> caches the task it returns, so a token one caller cancelled
    /// would leave every later read with a cancelled schema. A caller stops waiting on it with its own
    /// token instead, through <see cref="Task.WaitAsync(CancellationToken)"/>.
    /// </summary>
    private static async Task<JsonSchema> ReadManifestSchemaAsync()
    {
        await using Stream stream = typeof(ModuleArtifact).Assembly.GetManifestResourceStream(ManifestSchemaResource)
            ?? throw new InvalidOperationException($"The daemon carries no '{ManifestSchemaResource}'.");
        return await JsonSchema.FromJsonAsync(stream, CancellationToken.None);
    }

    /// <summary>The module API's manifest schema, which the daemon's project embeds from <c>schemas/module/v1/</c>.</summary>
    private const string ManifestSchemaResource = "Coanda.Daemon.Modules.manifest.schema.json";
}
