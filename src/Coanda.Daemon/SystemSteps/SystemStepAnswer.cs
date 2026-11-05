using System.Text.Json;
using System.Text.Json.Nodes;

namespace Coanda.Daemon.SystemSteps;

/// <summary>
/// How a system step answers its dispatch: done with a result document recorded on the flow run and
/// routed on, paused with a reason and a lever, or running, when the step goes on and records its
/// outcome later under the dispatch id. Done and paused are the outcomes a dispatch is recorded with.
/// The private constructor closes the hierarchy.
/// </summary>
public abstract record SystemStepAnswer
{
    /// <summary>The field every system step result routes on.</summary>
    public const string Verdict = "verdict";

    /// <summary>The verdict of a system step that did what it was asked.</summary>
    public const string DoneVerdict = "done";

    /// <summary>The verdict of a system step that ran and found the work failing, which a flow routes on like any other.</summary>
    public const string FailedVerdict = "failed";

    private SystemStepAnswer()
    {
    }

    /// <summary>The system step finished, whatever its verdict; <paramref name="Result"/> is the document the step routes on.</summary>
    public sealed record Done(JsonElement Result) : SystemStepAnswer;

    /// <summary>The system step stopped the flow run to wait; <paramref name="Lever"/> is what a person acts on.</summary>
    public sealed record Paused(string Reason, string Lever) : SystemStepAnswer;

    /// <summary>
    /// The system step goes on after its answer: the flow run stays running with no worker advancing
    /// it until the step's outcome is recorded under the dispatch id.
    /// </summary>
    public sealed record Running : SystemStepAnswer;

    /// <summary>A result whose verdict is <c>done</c>, naming the system step, with any further fields given.</summary>
    public static Done DoneWith(string systemStep, params (string Field, JsonNode? Value)[] fields) =>
        ResultWith(DoneVerdict, systemStep, fields);

    /// <summary>A result whose verdict is <c>failed</c>, naming the system step and carrying the <paramref name="reason"/>.</summary>
    public static Done FailedWith(string systemStep, string reason) =>
        ResultWith(FailedVerdict, systemStep, [("reason", reason)]);

    /// <summary>A result whose verdict is <paramref name="verdict"/>, naming the system step, with any further fields given.</summary>
    public static Done ResultWith(string verdict, string systemStep, params (string Field, JsonNode? Value)[] fields)
    {
        var result = new JsonObject
        {
            [Verdict] = verdict,
            ["systemStep"] = systemStep,
        };
        foreach ((string field, JsonNode? value) in fields)
        {
            result[field] = value;
        }

        return new Done(JsonSerializer.SerializeToElement(result));
    }
}
