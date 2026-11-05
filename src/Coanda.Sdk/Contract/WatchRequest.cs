namespace Coanda.Sdk.Contract;

/// <summary>
/// The watches the core compiles from the user's triggers for one project
/// (<c>watch-request.schema.json</c>). It replaces the module's whole set of watches for the
/// project, so an empty list means the module reports nothing for it.
/// </summary>
public sealed record WatchRequest(string Project, IReadOnlyList<Watch> Watches);
