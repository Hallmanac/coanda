using System.Text.Json;
using Coanda.Core.Agents;

namespace Coanda.Core.FlowRuns.Events;

/// <summary>
/// An agent answered the flow run's current agent step, with what the answer cost.
/// <paramref name="PromptHash"/> and <paramref name="TranscriptHash"/> are the <see cref="FileHash"/>
/// of the prompt the session's processes were given and of its transcript, both kept on the node that
/// ran the step. An answer recorded before they were carried replays with neither.
/// </summary>
public sealed record AgentStepAnswered(
    Guid FlowRunId,
    string Step,
    string Agent,
    string AgentHash,
    ModelSpec Model,
    JsonElement Answer,
    Measurement Measurement,
    DateTimeOffset AnsweredAt,
    string? PromptHash = null,
    string? TranscriptHash = null);
