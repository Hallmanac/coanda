namespace Coanda.Core.Definitions;

/// <summary>
/// The module-scope definition a customization was forked from, by its name and the version number
/// the store gave it. The module and module version it came from are the ones that definition records.
/// </summary>
public sealed record ForkBase(string Name, int Version);
