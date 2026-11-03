using Coanda.Core.Agents;

namespace Coanda.Daemon.Executors;

/// <summary>Everything one judgment's spawn takes: the agent, the assembled context, and the spawn's terms.</summary>
public sealed record JudgmentRequest(
    AgentDefinition Agent,
    string Context,
    string WorkingDirectory,
    SpawnCaps Caps,
    string AnswerSchema,
    string? CredentialProfile = null);
