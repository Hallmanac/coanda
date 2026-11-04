using Coanda.Core.Agents;

namespace Coanda.Daemon.Executors;

/// <summary>
/// Everything one agent step's spawn takes: the agent, the assembled context, the spawn's terms, and
/// what the session needs to reach the daemon through <c>coa</c>.
/// </summary>
public sealed record AgentStepRequest(
    AgentDefinition Agent,
    string Context,
    string WorkingDirectory,
    SpawnCaps Caps,
    string AnswerSchema,
    CredentialProfile? CredentialProfile = null,
    SessionAccess? Access = null);
