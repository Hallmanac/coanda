using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Registry;
using Coanda.Modules.Software;
using Coanda.Sdk.Contract;
using NJsonSchema;
using NJsonSchema.Validation;

namespace Coanda.Daemon.Modules;

/// <summary>
/// The modules the daemon hosts, each behind its host adapter, by name, with what each answered to
/// describe when the daemon started: the steps it brings, whose result schemas are read once then, and
/// its settings schema. Until <see cref="StartAsync"/> has asked every module, no module describes
/// anything. The software module is the only one, and it is switched on for every new project.
/// </summary>
public sealed class HostedModules(IReadOnlyDictionary<string, ModuleHostAdapter> adapters)
{
    /// <summary>The modules a project registered on this node is switched on for.</summary>
    public static readonly IReadOnlyList<string> Defaults = [SoftwareSteps.Module];

    private readonly Dictionary<string, Described> described = new(StringComparer.Ordinal);

    /// <summary>The name of every module the daemon hosts.</summary>
    public IReadOnlyCollection<string> Names => adapters.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Waits on every module's health and asks it to describe itself, refusing an answer that names a
    /// step without the module's own prefix or a result schema the daemon cannot read.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach ((string module, ModuleHostAdapter adapter) in adapters)
        {
            await adapter.HealthAsync(cancellationToken);
            DescribeAnswer answer = await adapter.DescribeAsync(cancellationToken);
            Dictionary<string, JsonSchema> results = new(StringComparer.Ordinal);
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
            }

            described[module] = new Described(adapter, answer, results);
        }
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
    /// The settings schema seam the daemon reads module settings through: each hosted module's settings
    /// schema as its describe answer declares it, and beside them <paramref name="others"/>, a test's
    /// stand-in modules, which may not share a name with a hosted module.
    /// </summary>
    public IModuleSettingsSchemas SettingsSchemas(IModuleSettingsSchemas? others)
    {
        var hosted = new ModuleSettingsSchemas(described.ToDictionary(d => d.Key, d => d.Value.Answer.SettingsSchema, StringComparer.Ordinal));
        return others is null
            ? hosted
            : new Combined(hosted, others);
    }

    private sealed record Described(ModuleHostAdapter Adapter, DescribeAnswer Answer, IReadOnlyDictionary<string, JsonSchema> Results);

    private sealed class Combined : IModuleSettingsSchemas
    {
        private readonly IModuleSettingsSchemas hosted;
        private readonly IModuleSettingsSchemas others;

        public Combined(IModuleSettingsSchemas hosted, IModuleSettingsSchemas others)
        {
            if (hosted.Modules.Intersect(others.Modules, StringComparer.Ordinal).FirstOrDefault() is { } both)
            {
                throw new DomainException($"Module '{both}' is hosted by the daemon and also given a settings schema of its own; a module's settings schema is its describe answer's.");
            }

            this.hosted = hosted;
            this.others = others;
        }

        public IReadOnlyCollection<string> Modules => [.. hosted.Modules, .. others.Modules];

        public SettingsSchema? Find(string module) => hosted.Find(module) ?? others.Find(module);
    }
}
