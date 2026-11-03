namespace Coanda.Core.Agents;

/// <summary>
/// The closed, vendor-neutral set of tool names an agent's tool set may hold. Names only: the
/// vendor adapter of phase 2 maps each one to the vendor's own tools. Kept apart from
/// <see cref="AgentDefinition"/> so the catalog can grow without the agent shape changing.
/// </summary>
public static class ToolCatalog
{
    public const string Read = "read";
    public const string Search = "search";
    public const string Edit = "edit";
    public const string Shell = "shell";
    public const string Web = "web";

    public static IReadOnlyList<string> Names { get; } = [Read, Search, Edit, Shell, Web];

    public static bool Holds(string? name) => name is not null && Names.Contains(name, StringComparer.Ordinal);
}
