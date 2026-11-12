namespace Coanda.Core.Agents;

/// <summary>
/// The answer schema an agent's steps end in: its name, the closed set of verdict values it allows,
/// and the JSON Schema document (draft-07 vocabulary). Flow transitions route on the verdict,
/// compared as an exact string. An agent resolves its own (<see cref="AgentDefinition.ResolveAnswerSchema"/>).
/// </summary>
public sealed record AnswerSchema(string Name, IReadOnlyList<string> Verdicts, string Document);
