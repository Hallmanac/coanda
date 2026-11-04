namespace Coanda.Core.Agents;

/// <summary>
/// One shipped answer schema: a name, the closed set of verdict values it allows, and the JSON
/// Schema document (draft-07 vocabulary). Flow transitions route on the verdict, compared as an
/// exact string.
/// </summary>
public sealed record AnswerSchema(string Name, IReadOnlyList<string> Verdicts, string Document);
