using System.Text.Json;
using Coanda.Sdk.Contract;
using Coanda.Sdk.Hosting;

namespace Coanda.Tests.Daemon.Modules;

/// <summary>
/// A stand-in module the daemon hosts in its process, which brings the one step <see cref="Step"/>,
/// <c>cms/publish</c>, whose argument schema is <paramref name="argumentSchema"/>, declares the settings
/// <paramref name="settings"/> (none when null), answers every dispatch done with the verdict
/// <c>done</c>, and records each dispatch it receives.
/// </summary>
internal sealed class StepModule(JsonElement argumentSchema, SettingsSchema? settings = null) : IModule
{
    public const string Name = "cms";
    public const string Step = "cms/publish";

    /// <summary>A required string argument, <c>channel</c>, and nothing the schema does not declare.</summary>
    public static JsonElement ChannelSchema { get; } = Json("""
        { "type": "object", "required": ["channel"], "properties": { "channel": { "type": "string" } }, "additionalProperties": false }
        """);

    private static readonly JsonElement ResultSchema = Json("""
        { "type": "object", "required": ["verdict"], "properties": { "verdict": { "type": "string" } } }
        """);

    private readonly Lock gate = new();
    private readonly List<DispatchRequest> dispatches = [];

    /// <summary>A module whose step requires the string argument <c>channel</c> and refuses any other.</summary>
    public StepModule()
        : this(ChannelSchema)
    {
    }

    /// <summary>Every dispatch received, in the order received.</summary>
    public IReadOnlyList<DispatchRequest> Dispatches
    {
        get
        {
            lock (gate)
            {
                return [.. dispatches];
            }
        }
    }

    public Task HealthAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<DescribeAnswer> DescribeAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new DescribeAnswer(
            ModuleApi.Version, "1.0.0", [new StepDescription(Step, argumentSchema, ResultSchema)], [], [], settings ?? new SettingsSchema([])));

    public Task<DispatchAnswer> DispatchAsync(DispatchRequest request, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            dispatches.Add(request);
        }

        return Task.FromResult<DispatchAnswer>(new DispatchAnswer.Done(new StepResult("done")));
    }

    public Task<StatusAnswer> StatusAsync(StatusRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<StatusAnswer>(new StatusAnswer.Unknown());

    public Task StopAsync(StopRequest request, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task FeedAsync(FeedDelivery delivery, CancellationToken cancellationToken) => Task.CompletedTask;

    public static JsonElement Json(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
