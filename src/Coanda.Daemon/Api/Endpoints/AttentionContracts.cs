using Coanda.Daemon.Attention;

namespace Coanda.Daemon.Api.Endpoints;

/// <summary>
/// A paused flow run this node started, as the loopback API carries it: its project, its id, the parcel it
/// runs on and that parcel's title, its flow's name, the step it paused on, the pause's reason and lever,
/// and the time its latest pause was recorded.
/// </summary>
public sealed record AttentionView(
    string Project,
    Guid FlowRunId,
    Guid ParcelId,
    string ParcelTitle,
    string FlowName,
    string Step,
    string Reason,
    string Lever,
    DateTimeOffset PausedAt)
{
    public static AttentionView From(PausedFlowRun paused) => new(
        paused.Project, paused.FlowRunId, paused.ParcelId, paused.ParcelTitle, paused.FlowName,
        paused.Step, paused.Reason, paused.Lever, paused.PausedAt);
}
