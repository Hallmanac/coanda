using Coanda.Core.Agents;

namespace Coanda.Daemon.Executors;

/// <summary>
/// Everything one agent step's spawn takes: the agent, the assembled context, the spawn's terms,
/// what the session needs to reach the daemon through <c>coa</c>, and the skills its agent names, as
/// the runner resolved them from the modules switched on for the parcel's project.
/// </summary>
public sealed record AgentStepRequest(
    AgentDefinition Agent,
    string Context,
    string WorkingDirectory,
    SpawnCaps Caps,
    string AnswerSchema,
    CredentialProfile? CredentialProfile = null,
    SessionAccess? Access = null)
{
    /// <summary>The skills the session is given, each once, in the order the agent names them; none when it names none.</summary>
    public IReadOnlyList<SessionSkill> Skills { get; init; } = [];
}
