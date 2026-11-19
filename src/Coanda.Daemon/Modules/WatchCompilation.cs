using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Projects;
using Coanda.Core.Registry;
using Coanda.Core.Triggers;
using Coanda.Sdk.Contract;
using NJsonSchema;
using NJsonSchema.Validation;

namespace Coanda.Daemon.Modules;

/// <summary>
/// Compiles a project's triggers into the watch request for one module: one watch for each trigger
/// whose event kind the module's name prefixes, carrying the trigger's kind and one filter per filter
/// of the trigger, with two triggers that compile to the same kind and filters, in any order, giving
/// one watch.
/// </summary>
/// <remarks>
/// A trigger whose kind is a plain name or a flow run's end names no module and compiles to no watch,
/// and so does a module-scope trigger in a project the module that ships it is not switched on for,
/// since it does not fire there. A trigger the module's describe answer cannot serve (its kind is not
/// listed, or one of its filter fields is not declared watchable for that kind), one whose watch would
/// break the module API's watch request schema whatever describe lists, and one whose content cannot
/// be read are left out and returned with the reason, so one bad trigger never costs the project its
/// other watches.
/// </remarks>
public static class WatchCompilation
{
    /// <summary>
    /// The watch request <paramref name="triggers"/>, the triggers resolved for <paramref name="project"/>,
    /// compile to for <paramref name="module"/>, and the triggers it leaves out: those on the module's
    /// kinds it cannot send, and any whose content cannot be read, whose kind is then unknown.
    /// </summary>
    public static CompiledWatches Compile(
        Project project, string module, DescribeAnswer describe, IEnumerable<Definition> triggers, JsonSchema schema)
    {
        SortedDictionary<string, Watch> watches = new(StringComparer.Ordinal);
        List<DroppedTrigger> dropped = [];
        foreach (Definition definition in triggers.Where(d => d.Kind == DefinitionKind.Trigger && d.Status == DefinitionStatus.Published))
        {
            if (definition.Scope == DefinitionScope.Module && definition.Module is { } shipper && !project.IsSwitchedOn(shipper))
            {
                continue;
            }

            TriggerDefinition trigger;
            try
            {
                trigger = TriggerDefinition.FromJson(definition.Content);
            }
            catch (Exception failure) when (failure is DomainException or JsonException)
            {
                dropped.Add(new DroppedTrigger(definition.Name, $"its content cannot be read as a trigger: {failure.Message}"));
                continue;
            }

            if (!string.Equals(ModuleNames.PrefixOf(trigger.EventKind), module, StringComparison.Ordinal))
            {
                continue;
            }

            var watch = new Watch(
                trigger.EventKind,
                [.. trigger.Filters.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => new WatchFilter(f.Key, f.Value))]);
            if (RefusalOf(project.Name, module, watch, describe, schema) is { } reason)
            {
                dropped.Add(new DroppedTrigger(definition.Name, reason));
                continue;
            }

            watches.TryAdd(KeyOf(watch), watch);
        }

        return new CompiledWatches(new WatchRequest(project.Name, [.. watches.Values]), dropped);
    }

    /// <summary>Why <paramref name="watch"/> cannot be sent to the module, or null when it can.</summary>
    private static string? RefusalOf(string project, string module, Watch watch, DescribeAnswer describe, JsonSchema schema)
    {
        ICollection<ValidationError> failures = schema.Validate(JsonSerializer.Serialize(new WatchRequest(project, [watch]), ModuleJson.Options));
        if (failures.Count > 0)
        {
            return $"its watch would break the module API's watch request schema: {string.Join("; ", failures.Select(f => $"{f.Kind} at {f.Path ?? "#"}"))}";
        }

        if (describe.EventKinds.FirstOrDefault(k => string.Equals(k.Kind, watch.Kind, StringComparison.Ordinal)) is not { } kind)
        {
            return $"module '{module}' describes no event kind '{watch.Kind}'";
        }

        return watch.Filters.FirstOrDefault(f => !kind.WatchableFields.Contains(f.Field, StringComparer.Ordinal)) is { } unwatchable
            ? $"module '{module}' does not declare the field '{unwatchable.Field}' watchable for '{watch.Kind}'"
            : null;
    }

    /// <summary>The same key for every watch of the same kind and filters, whatever order the filters are in.</summary>
    private static string KeyOf(Watch watch) =>
        JsonSerializer.Serialize(new object[] { watch.Kind, watch.Filters.OrderBy(f => f.Field, StringComparer.Ordinal).Select(f => new[] { f.Field, f.Value }) });
}

/// <summary>The watch request a project's triggers compile to for a module, and the triggers it leaves out.</summary>
public sealed record CompiledWatches(WatchRequest Request, IReadOnlyList<DroppedTrigger> Dropped);

/// <summary>A trigger compiled to no watch, by its name, and why.</summary>
public sealed record DroppedTrigger(string Trigger, string Reason);
