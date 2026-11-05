using System.Text.Json;
using System.Text.Json.Nodes;
using Coanda.Sdk.Contract;

namespace Coanda.Modules.Software.Steps;

/// <summary>
/// The done answers the module's steps give: a result with a verdict, the step's name under
/// <c>systemStep</c>, and the step's further fields.
/// </summary>
public static class Results
{
    /// <summary>The verdict of a step that did what it was asked.</summary>
    public const string DoneVerdict = "done";

    /// <summary>The verdict of a step that ran and found the work failing, which a flow routes on like any other.</summary>
    public const string FailedVerdict = "failed";

    /// <summary>A result whose verdict is <c>done</c>, naming the step, with any further fields given.</summary>
    public static DispatchAnswer.Done DoneWith(string step, params (string Field, JsonNode? Value)[] fields) =>
        With(DoneVerdict, step, fields);

    /// <summary>A result whose verdict is <c>failed</c>, naming the step and carrying the <paramref name="reason"/>.</summary>
    public static DispatchAnswer.Done FailedWith(string step, string reason) =>
        With(FailedVerdict, step, ("reason", reason));

    /// <summary>A result whose verdict is <paramref name="verdict"/>, naming the step, with any further fields given.</summary>
    public static DispatchAnswer.Done With(string verdict, string step, params (string Field, JsonNode? Value)[] fields)
    {
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["systemStep"] = JsonSerializer.SerializeToElement(step),
        };
        foreach ((string field, JsonNode? value) in fields)
        {
            values[field] = JsonSerializer.SerializeToElement(value);
        }

        return new DispatchAnswer.Done(new StepResult(verdict) { Fields = values });
    }
}
