using Coanda.Core.Definitions;

namespace Coanda.Daemon.Modules;

/// <summary>One definition a module brings, with the file it was read from, or null when it was given directly.</summary>
public sealed record ModuleArtifactDefinition(ModuleDefinition Definition, string? File);
