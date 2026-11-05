using System.Text.Json;
using Coanda.Core.Definitions;
using Coanda.Daemon.Modules;
using Coanda.Sdk.Contract;
using Coanda.Tests.Sdk;

namespace Coanda.Tests;

/// <summary>
/// The software module's manifest and definitions, read from the JSON files in its project, the
/// files the daemon imports at start: <see cref="SoftwareTypes"/>, <see cref="SoftwareFlows"/>, and
/// <see cref="SoftwareAgents"/> hold the definitions those files describe.
/// The lists are read each time, since those types read their files through this one.
/// </summary>
public static class SoftwareDefinitions
{
    /// <summary>The software module's project, which holds its <c>module.json</c> and definitions folder.</summary>
    public static string ProjectPath { get; } = RepositoryFiles.PathOf("src/Coanda.Modules.Software");

    public static ModuleManifest Manifest { get; } =
        JsonSerializer.Deserialize<ModuleManifest>(File.ReadAllText(Path.Combine(ProjectPath, ModuleArtifact.ManifestFile)), ModuleJson.Options)
        ?? throw new InvalidOperationException("The software module's manifest is empty.");

    /// <summary>The module's parcel types, as module definitions.</summary>
    public static IReadOnlyList<ModuleDefinition> ParcelTypes => [.. SoftwareTypes.All.Select(ModuleDefinition.FromParcelType)];

    /// <summary>The module's flows, as module definitions.</summary>
    public static IReadOnlyList<ModuleDefinition> Flows => [.. SoftwareFlows.All.Select(ModuleDefinition.FromFlow)];

    /// <summary>The module's agents, as module definitions.</summary>
    public static IReadOnlyList<ModuleDefinition> Agents => [.. SoftwareAgents.All.Select(ModuleDefinition.FromAgent)];

    /// <summary>Every definition the module brings, in the order the daemon imports them.</summary>
    public static IReadOnlyList<ModuleDefinition> All => [.. ParcelTypes, .. Flows, .. Agents];

    /// <summary><paramref name="definitions"/> as the software module brings them, at the version its manifest names.</summary>
    public static ModuleArtifact Artifact(IEnumerable<ModuleDefinition> definitions) =>
        ModuleArtifact.Of(Manifest.Name, Manifest.Version, definitions);

    /// <summary>The text of the definition file <paramref name="name"/> in the kind folder <paramref name="folder"/>.</summary>
    public static string Read(string folder, string name) =>
        File.ReadAllText(Path.Combine(ProjectPath, ModuleArtifact.DefinitionsFolder, folder, $"{name}.json"));
}
