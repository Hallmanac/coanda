using System.Text.Json;
using System.Text.Json.Nodes;

namespace Coanda.Daemon.Actions;

/// <summary>
/// How an action step ended: a result document recorded on the flow and routed on, or a pause
/// with a reason and a lever. The private constructor closes the hierarchy.
/// </summary>
public abstract record ActionOutcome
{
    /// <summary>The field every action result routes on.</summary>
    public const string Verdict = "verdict";

    /// <summary>The verdict of an action that did what it was asked.</summary>
    public const string DoneVerdict = "done";

    /// <summary>The verdict of an action that ran and found the work failing, which a contour routes on like any other.</summary>
    public const string FailedVerdict = "failed";

    private ActionOutcome()
    {
    }

    /// <summary>The action finished, whatever its verdict; <paramref name="Result"/> is the document the step routes on.</summary>
    public sealed record Done(JsonElement Result) : ActionOutcome;

    /// <summary>The action stopped the flow to wait; <paramref name="Lever"/> is what a person acts on.</summary>
    public sealed record Paused(string Reason, string Lever) : ActionOutcome;

    /// <summary>A result whose verdict is <c>done</c>, naming the action, with any further fields given.</summary>
    public static Done DoneWith(string action, params (string Field, JsonNode? Value)[] fields) =>
        WithVerdict(DoneVerdict, action, fields);

    /// <summary>A result whose verdict is <c>failed</c>, naming the action and carrying the <paramref name="reason"/>.</summary>
    public static Done FailedWith(string action, string reason) =>
        WithVerdict(FailedVerdict, action, [("reason", reason)]);

    private static Done WithVerdict(string verdict, string action, (string Field, JsonNode? Value)[] fields)
    {
        var result = new JsonObject
        {
            [Verdict] = verdict,
            ["action"] = action,
        };
        foreach ((string field, JsonNode? value) in fields)
        {
            result[field] = value;
        }

        return new Done(JsonSerializer.SerializeToElement(result));
    }
}
