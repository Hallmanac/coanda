using System.Text.Json.Serialization;

namespace Coanda.Sdk.Contract;

/// <summary>
/// Where a dispatch stands (<c>status.schema.json#/definitions/answer</c>): running with optional
/// progress text, done with a result, paused with a reason and a lever, or unknown to the module, on
/// which the daemon dispatches again under the same id. The <c>answer</c> property names the kind.
/// The private constructor closes the hierarchy.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "answer")]
[JsonDerivedType(typeof(StatusAnswer.Running), "running")]
[JsonDerivedType(typeof(StatusAnswer.Done), "done")]
[JsonDerivedType(typeof(StatusAnswer.Paused), "paused")]
[JsonDerivedType(typeof(StatusAnswer.Unknown), "unknown")]
public abstract record StatusAnswer
{
    private StatusAnswer()
    {
    }

    /// <summary>The step is still going on; <paramref name="Progress"/> is free text for a person, never routed on.</summary>
    public sealed record Running(string? Progress = null) : StatusAnswer;

    /// <summary>The step finished, whatever its verdict.</summary>
    public sealed record Done(StepResult Result) : StatusAnswer;

    /// <summary>The step stopped the flow run to wait; <paramref name="Lever"/> is what a person acts on.</summary>
    public sealed record Paused(string Reason, string Lever) : StatusAnswer;

    /// <summary>The module does not know the dispatch.</summary>
    public sealed record Unknown : StatusAnswer;
}
