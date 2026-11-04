namespace Coanda.Core.Flows.Events;

/// <summary>A flow entered a step; <paramref name="Performer"/> is the agent or action the step names.</summary>
public sealed record StepStarted(
    Guid FlowId,
    string Step,
    StepKind Kind,
    string Performer,
    DateTimeOffset StartedAt);
