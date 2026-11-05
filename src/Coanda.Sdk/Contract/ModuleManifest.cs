namespace Coanda.Sdk.Contract;

/// <summary>
/// A module artifact's <c>module.json</c> (<c>manifest.schema.json</c>). <paramref name="Start"/> is
/// the program, relative to the artifact directory, followed by its arguments.
/// </summary>
public sealed record ModuleManifest(
    string Name,
    string Version,
    int ModuleApiVersion,
    IReadOnlyList<string> Start,
    string SourceUrl,
    string SourceCommit);
