namespace Coanda.Core.Agents;

/// <summary>The outcome of checking an answer against its schema: valid, or the list of failures.</summary>
public sealed record AnswerValidation(IReadOnlyList<string> Failures)
{
    public bool IsValid => Failures.Count == 0;

    public static AnswerValidation Valid { get; } = new([]);

    public static AnswerValidation Invalid(params string[] failures) => new(failures);
}
