namespace Coanda.Core.Registry;

/// <summary>
/// Who is calling. Every command and query carries one; nothing runs anonymously. A principal
/// resolved from an agent step's session token carries the <see cref="Session"/> it is bound to, and
/// records on that flow run alone while that launch runs. A principal resolved from a module token
/// carries the <see cref="Module"/> it acts for, and acts only in the projects that module is
/// switched on for. Every other principal carries neither.
/// </summary>
public sealed record Principal(string Id, Role Role, SessionBinding? Session = null, string? Module = null)
{
    public static readonly Principal LocalOwner = new("owner", Role.Owner);

    /// <summary>The Agent-role principal of an agent step's session, bound to its flow run and its launch.</summary>
    public static Principal ForSession(SessionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return new Principal($"session:{binding.LaunchId}", Role.Agent, binding);
    }

    /// <summary>
    /// The Module-role principal of the module <paramref name="module"/>, a module's name
    /// (<see cref="ModuleNames.IsModuleName"/>); any other name is refused.
    /// </summary>
    public static Principal ForModule(string module)
    {
        string name = ModuleNames.Require(module);
        return new Principal($"module:{name}", Role.Module, Module: name);
    }
}

public enum Role
{
    Owner,
    Member,
    Agent,

    /// <summary>
    /// A module calling the loopback API with its module token. The role is a correctness boundary
    /// against a steered module, not a security layer against a malicious one: a module runs as the
    /// daemon's own OS user.
    /// </summary>
    Module,
}
