using Coanda.Core.Definitions;
using Coanda.Core.Triggers;

namespace Coanda.Daemon.Triggers;

/// <summary>
/// A published trigger definition that resolution returned for a project and that fires there, with its
/// content read and its name as <see cref="TriggerKey"/> gives it.
/// </summary>
internal sealed record ResolvedTrigger(Definition Definition, TriggerDefinition Content)
{
    /// <summary>The trigger's name in what it records, alike on every node.</summary>
    public string Key { get; } = TriggerKey.Of(Definition);
}
