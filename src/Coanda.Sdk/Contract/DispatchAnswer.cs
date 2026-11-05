using System.Text.Json.Serialization;

namespace Coanda.Sdk.Contract;

/// <summary>
/// How a module answers a dispatch (<c>dispatch.schema.json#/definitions/answer</c>): done with a
/// result, paused with a reason and a lever, or running. The <c>answer</c> property names the kind.
/// The private constructor closes the hierarchy.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "answer")]
[JsonDerivedType(typeof(DispatchAnswer.Done), "done")]
[JsonDerivedType(typeof(DispatchAnswer.Paused), "paused")]
[JsonDerivedType(typeof(DispatchAnswer.Running), "running")]
public abstract record DispatchAnswer
{
    private DispatchAnswer()
    {
    }

    /// <summary>The step finished, whatever its verdict.</summary>
    public sealed record Done(StepResult Result) : DispatchAnswer;

    /// <summary>The step stopped the flow run to wait; <paramref name="Lever"/> is what a person acts on.</summary>
    public sealed record Paused(string Reason, string Lever) : DispatchAnswer;

    /// <summary>The step goes on; the module records its outcome later through a core command naming the dispatch id.</summary>
    public sealed record Running : DispatchAnswer;
}
