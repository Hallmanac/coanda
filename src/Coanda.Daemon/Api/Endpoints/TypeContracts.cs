namespace Coanda.Daemon.Api.Endpoints;

public sealed record TypeView(string Name, string Hash, IReadOnlyList<string> States, IReadOnlyList<string> Resolutions);
