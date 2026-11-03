namespace Coanda.Core.Agents;

/// <summary>
/// An agent's persona in five parts, each plain prose. The adapter of a vendor renders them into
/// that vendor's system prompt; there is no marker-line grammar, since the JSON answer is the
/// only output contract.
/// </summary>
public sealed record AgentPersona(string Role, string Purpose, string Output, string Rules, string Prohibitions);
