using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Flows;
using Coanda.Core.Registry;
using Coanda.Modules.Software;
using Coanda.Sdk.Contract;
using NJsonSchema;
using NJsonSchema.Validation;

namespace Coanda.Daemon.Modules;

/// <summary>
/// The modules the daemon hosts, each behind its host adapter, by name, with what each answered to
/// describe when the daemon started: the steps it brings, whose argument and result schemas are read
/// once then, the core event kinds it subscribes to, and its settings schema, whose settings' schemas are
/// read then too, so a schema that cannot be read or refers outside itself stops the start. Until
/// <see cref="StartAsync"/> has asked every module, no module describes anything. The software module is switched on for every new
/// project; a test may host stand-in modules beside it.
/// </summary>
/// <remarks>
/// <paramref name="others"/> are the settings schemas of modules the daemon does not host, a test's
/// stand-ins, which may not share a name with a hosted module. <see cref="SettingsSchemas"/> reads the
/// hosted modules' describe answers whenever it is asked, so a service built before the daemon has
/// asked them sees their settings once it has.
/// </remarks>
public sealed class HostedModules(IReadOnlyDictionary<string, ModuleHostAdapter> adapters, IModuleSettingsSchemas? others = null)
{
    /// <summary>The modules a project registered on this node is switched on for.</summary>
    public static readonly IReadOnlyList<string> Defaults = [SoftwareSteps.Module];

    private readonly Dictionary<string, Described> described = new(StringComparer.Ordinal);
    private readonly IModuleSettingsSchemas? standIns = others;
    private IModuleSettingsSchemas hostedSchemas = ModuleSettingsSchemas.None;

    /// <summary>The name of every module the daemon hosts.</summary>
    public IReadOnlyCollection<string> Names => adapters.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Waits on every module's health and asks it to describe itself, refusing an answer that names a
    /// step without the module's own prefix, or an argument, result, or setting's schema the daemon
    /// cannot read or that refers to anything outside its own document, and a hosted module that a
    /// stand-in's settings schema also names. Each schema is read from its own text alone: nothing is
    /// fetched or read to resolve a reference.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach ((string module, ModuleHostAdapter adapter) in adapters)
        {
            await adapter.HealthAsync(cancellationToken);
            DescribeAnswer answer = await adapter.DescribeAsync(cancellationToken);
            Dictionary<string, JsonSchema> results = new(StringComparer.Ordinal);
            Dictionary<string, JsonSchema> arguments = new(StringComparer.Ordinal);
            foreach (StepDescription step in answer.Steps)
            {
                if (!string.Equals(ModuleNames.PrefixOf(step.Name), module, StringComparison.Ordinal))
                {
                    throw new DomainException(
                        $"Module '{module}' describes the step '{step.Name}'; a module's steps carry its own name before a slash, '{module}/<step>'.");
                }

                results[step.Name] = await LocalSchemas.ReadAsync(
                    step.ResultSchema.GetRawText(),
                    reference => $"The result schema of step '{step.Name}' of module '{module}' refers to '{reference}', outside itself; a result schema refers only to its own definitions.",
                    reason => $"The result schema of step '{step.Name}' of module '{module}' cannot be read: {reason}",
                    cancellationToken);
                if (step.ArgumentSchema.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
                {
                    arguments[step.Name] = await LocalSchemas.ReadAsync(
                        step.ArgumentSchema.GetRawText(),
                        reference => $"The argument schema of step '{step.Name}' of module '{module}' refers to '{reference}', outside itself; an argument schema refers only to its own definitions.",
                        reason => $"The argument schema of step '{step.Name}' of module '{module}' cannot be read: {reason}",
                        cancellationToken);
                }
            }

            foreach (ReplicatedSetting setting in answer.SettingsSchema.Replicated)
            {
                await LocalSchemas.ReadAsync(
                    setting.Schema.GetRawText(),
                    reference => $"The schema of setting '{setting.Name}' in the settings schema of module '{module}' refers to '{reference}', outside itself; a setting's schema refers only to its own definitions.",
                    reason => $"The schema of setting '{setting.Name}' in the settings schema of module '{module}' cannot be read: {reason}",
                    cancellationToken);
            }

            described[module] = new Described(adapter, answer, results, arguments);
        }

        var hosted = new ModuleSettingsSchemas(described.ToDictionary(d => d.Key, d => d.Value.Answer.SettingsSchema, StringComparer.Ordinal));
        if (standIns is not null && hosted.Modules.Intersect(standIns.Modules, StringComparer.Ordinal).FirstOrDefault() is { } both)
        {
            throw new DomainException($"Module '{both}' is hosted by the daemon and also given a settings schema of its own; a module's settings schema is its describe answer's.");
        }

        hostedSchemas = hosted;
    }

    /// <summary>The adapter of the module <paramref name="module"/>, or null when the daemon hosts no module by that name.</summary>
    public ModuleHostAdapter? AdapterOf(string module) => described.GetValueOrDefault(module)?.Adapter;

    /// <summary>What the module <paramref name="module"/> answered to describe, or null when the daemon hosts none by that name.</summary>
    public DescribeAnswer? DescribeOf(string module) => described.GetValueOrDefault(module)?.Answer;

    /// <summary>Whether the module <paramref name="module"/> describes the step <paramref name="step"/>.</summary>
    public bool Provides(string module, string step) =>
        described.GetValueOrDefault(module) is { } found && found.Results.ContainsKey(step);

    /// <summary>The steps the modules <paramref name="modules"/> describe, those the daemon hosts.</summary>
    public IReadOnlySet<string> StepsOf(IEnumerable<string> modules) =>
        modules
            .Select(m => described.GetValueOrDefault(m))
            .OfType<Described>()
            .SelectMany(d => d.Results.Keys)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The ways <paramref name="result"/> fails the result schema of the module's step
    /// <paramref name="step"/>; none when it meets it.
    /// </summary>
    public IReadOnlyList<string> RefusalsOf(string module, string step, JsonElement result)
    {
        JsonSchema schema = described.GetValueOrDefault(module)?.Results.GetValueOrDefault(step)
            ?? throw new InvalidOperationException($"Module '{module}' describes no step '{step}'.");
        ICollection<ValidationError> failures = schema.Validate(result.GetRawText());
        return [.. failures.Select(f => $"{f.Kind} at {f.Path ?? "#"}")];
    }

    /// <summary>
    /// The ways <paramref name="arguments"/>, a flow step's arguments, fail the argument schema of the
    /// module's step <paramref name="step"/>; none when they meet it, when the step declares no argument
    /// schema, or when the daemon hosts no module by that name that describes the step.
    /// </summary>
    public IReadOnlyList<StepArgumentComplaint> ArgumentComplaintsOf(string module, string step, IReadOnlyDictionary<string, JsonElement> arguments)
    {
        if (described.GetValueOrDefault(module)?.Arguments.GetValueOrDefault(step) is not { } schema)
        {
            return [];
        }

        ICollection<ValidationError> failures = schema.Validate(JsonSerializer.Serialize(arguments));
        return [.. failures.Select(StepArgumentComplaints.Of)];
    }

    /// <summary>
    /// The settings schema seam the daemon reads module settings through: each hosted module's settings
    /// schema as its describe answer declares it, once it has described itself, and beside them the
    /// stand-in modules' schemas this was built with.
    /// </summary>
    public IModuleSettingsSchemas SettingsSchemas => new View(this);

    private sealed record Described(
        ModuleHostAdapter Adapter, DescribeAnswer Answer, IReadOnlyDictionary<string, JsonSchema> Results, IReadOnlyDictionary<string, JsonSchema> Arguments);

    private sealed class View(HostedModules owner) : IModuleSettingsSchemas
    {
        public IReadOnlyCollection<string> Modules => [.. owner.hostedSchemas.Modules, .. owner.standIns?.Modules ?? []];

        public SettingsSchema? Find(string module) => owner.hostedSchemas.Find(module) ?? owner.standIns?.Find(module);
    }
}
