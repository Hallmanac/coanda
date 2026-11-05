using System.Text.Json;
using Coanda.Core.Definitions;
using Coanda.Daemon.Modules;

namespace Coanda.Tests.Daemon.Modules;

/// <summary>
/// A stand-in module's directory, laid out as the software module's is: a <c>module.json</c> naming
/// the module and its version, and a definitions folder holding one file per definition given, in the
/// folder of its kind. Removed when disposed.
/// </summary>
internal sealed class StandInModuleDirectory : IDisposable
{
    private StandInModuleDirectory(string path) => Path = path;

    public string Path { get; }

    public string DefinitionsPath => System.IO.Path.Combine(Path, ModuleArtifact.DefinitionsFolder);

    public static StandInModuleDirectory Write(string module, string version, params ModuleDefinition[] definitions)
    {
        var directory = new StandInModuleDirectory(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "coanda-tests", $"module-{Guid.NewGuid():N}"));
        Directory.CreateDirectory(directory.DefinitionsPath);
        directory.WriteManifest(module, version);
        foreach (ModuleDefinition definition in definitions)
        {
            directory.Write(definition);
        }

        return directory;
    }

    public void WriteManifest(string module, string version) =>
        File.WriteAllText(
            System.IO.Path.Combine(Path, ModuleArtifact.ManifestFile),
            JsonSerializer.Serialize(new
            {
                name = module,
                version,
                moduleApiVersion = 1,
                start = new[] { module },
                sourceUrl = "https://example.com/module",
                sourceCommit = "0000000",
            }));

    /// <summary>Writes <paramref name="definition"/> to the file named for it in its kind's folder, and returns that file's path.</summary>
    public string Write(ModuleDefinition definition)
    {
        string folder = ModuleArtifact.KindFolders.Single(k => k.Kind == definition.Kind).Folder;
        return WriteFile($"{folder}/{definition.Name}.json", definition.Content);
    }

    /// <summary>Writes <paramref name="text"/> to <paramref name="relative"/>, a path inside the definitions folder, and returns its full path.</summary>
    public string WriteFile(string relative, string text)
    {
        string path = System.IO.Path.Combine([DefinitionsPath, .. relative.Split('/')]);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path) ?? DefinitionsPath);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
