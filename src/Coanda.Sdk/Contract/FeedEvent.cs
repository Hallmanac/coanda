using System.Text.Json.Serialization;

namespace Coanda.Sdk.Contract;

/// <summary>
/// One core event in a feed delivery, carrying the project it happened in and its position in the
/// node's event sequence, and the ids of what it is about. The <c>kind</c> property names its
/// <see cref="CoreEventKind"/>. The private constructor closes the hierarchy.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(FeedEvent.ParcelEntered), "parcel-entered")]
[JsonDerivedType(typeof(FeedEvent.FlowRunEnded), "flow-run-ended")]
[JsonDerivedType(typeof(FeedEvent.FlowRunStopped), "flow-run-stopped")]
[JsonDerivedType(typeof(FeedEvent.FlowRunPaused), "flow-run-paused")]
[JsonDerivedType(typeof(FeedEvent.ParcelFieldSet), "parcel-field-set")]
public abstract record FeedEvent
{
    private FeedEvent(string project, long sequence)
    {
        Project = project;
        Sequence = sequence;
    }

    /// <summary>The project the event happened in.</summary>
    public string Project { get; }

    /// <summary>The event's position in the node's event sequence.</summary>
    public long Sequence { get; }

    /// <summary>A parcel entered <paramref name="Stage"/>, by being created in it or moved into it.</summary>
    public sealed record ParcelEntered(string Project, long Sequence, Guid ParcelId, string Stage) : FeedEvent(Project, Sequence);

    /// <summary>A flow run reached the end of its flow.</summary>
    public sealed record FlowRunEnded(string Project, long Sequence, Guid FlowRunId) : FeedEvent(Project, Sequence);

    /// <summary>Someone stopped a live flow run before its end.</summary>
    public sealed record FlowRunStopped(string Project, long Sequence, Guid FlowRunId) : FeedEvent(Project, Sequence);

    /// <summary>A flow run stopped to wait for a person.</summary>
    public sealed record FlowRunPaused(string Project, long Sequence, Guid FlowRunId) : FeedEvent(Project, Sequence);

    /// <summary>A value was recorded in the parcel's <paramref name="Field"/>.</summary>
    public sealed record ParcelFieldSet(string Project, long Sequence, Guid ParcelId, string Field) : FeedEvent(Project, Sequence);
}
