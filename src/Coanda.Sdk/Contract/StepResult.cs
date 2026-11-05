using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coanda.Sdk.Contract;

/// <summary>
/// The result document a done answer carries, which the flow's routes read: a
/// <paramref name="Verdict"/>, and whatever further fields the step's declared result schema gives it.
/// </summary>
public sealed record StepResult(string Verdict)
{
    /// <summary>The result's fields beyond its verdict, or null when it has none.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Fields { get; init; }
}
