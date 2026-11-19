namespace Coanda.Daemon.Modules;

/// <summary>
/// The modules a project registered on this node is switched on for, and that a project registered before
/// modules were has switched on at start. The host supplies them (<see cref="Api.DaemonOptions.DefaultModules"/>).
/// </summary>
public sealed record DefaultModules(IReadOnlyList<string> Names)
{
    /// <summary>No default modules: a project is registered with none switched on.</summary>
    public static readonly DefaultModules None = new([]);
}
