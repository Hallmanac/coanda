using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coanda.Sdk.Contract;

/// <summary>
/// The result document a done answer carries, which the flow's routes read: a
/// <paramref name="Verdict"/>, and whatever further fields the step's declared result schema gives it.
/// </summary>
public sealed record StepResult(string Verdict) : IJsonOnSerializing
{
    /// <summary>The name the verdict is written under, which no further field may take.</summary>
    private const string VerdictName = "verdict";

    /// <summary>The result's fields beyond its verdict, or null when it has none.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Fields { get; init; }

    /// <summary>
    /// Refuses to write a result whose <see cref="Fields"/> hold a <c>verdict</c>, which would write
    /// the verdict twice and leave which one a reader keeps to its parser.
    /// </summary>
    void IJsonOnSerializing.OnSerializing()
    {
        if (Fields is { } fields && fields.ContainsKey(VerdictName))
        {
            throw new InvalidOperationException($"A step result's fields cannot hold '{VerdictName}'; the result's verdict is {nameof(Verdict)}.");
        }
    }
}
