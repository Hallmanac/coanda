namespace Coanda.Daemon.Triggers;

/// <summary>
/// Where the trigger loop has read the record up to: the global sequence of the last event it
/// handled. One document in the node tenant, so the position survives a restart.
/// </summary>
public sealed class TriggerPosition
{
    public const string LoopId = "trigger-loop";

    public string Id { get; set; } = LoopId;

    public long Sequence { get; set; }
}
