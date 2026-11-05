namespace Coanda.Daemon.Modules;

/// <summary>
/// Where the module feed has read the record up to for one module: the global sequence of the last
/// event that module has handled or been passed over. One document per module in the node tenant, so the
/// position survives a restart.
/// </summary>
public sealed class FeedPosition
{
    /// <summary>The document id of <paramref name="module"/>'s position.</summary>
    public static string IdOf(string module) => $"module-feed:{module}";

    public string Id { get; set; } = string.Empty;

    public string Module { get; set; } = string.Empty;

    public long Sequence { get; set; }
}
