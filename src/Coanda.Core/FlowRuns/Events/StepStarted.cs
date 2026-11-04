namespace Coanda.Core.FlowRuns.Events;

/// <summary>A flow run entered a step; <paramref name="Performer"/> is the agent or system step the step names.</summary>
public sealed record StepStarted(
    Guid FlowRunId,
    string Step,
    StepKind Kind,
    string Performer,
    DateTimeOffset StartedAt);
