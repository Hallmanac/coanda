namespace Coanda.Daemon.Api.Endpoints;

public sealed record TypeView(string Name, string Hash, IReadOnlyList<string> Stages, IReadOnlyList<string> Resolutions);
