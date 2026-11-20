namespace Coanda.Daemon.Modules;

/// <summary>
/// One skill a module's directory ships: the folder of its skills folder named <see cref="Name"/>,
/// holding a <c>SKILL.md</c>, at <see cref="Directory"/>.
/// </summary>
public sealed record ModuleSkill(string Name, string Directory);
