namespace Coanda.Core.Registry;

/// <summary>
/// Who is calling. Every command and query carries one; nothing runs anonymously. A principal
/// resolved from an agent step's session token carries the <see cref="Session"/> it is bound to, and
/// records on that flow run alone while that launch runs; every other principal carries none.
/// </summary>
public sealed record Principal(string Id, Role Role, SessionBinding? Session = null)
{
    public static readonly Principal LocalOwner = new("owner", Role.Owner);

    /// <summary>The Agent-role principal of an agent step's session, bound to its flow run and its launch.</summary>
    public static Principal ForSession(SessionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return new Principal($"session:{binding.LaunchId}", Role.Agent, binding);
    }
}

public enum Role
{
    Owner,
    Member,
    Agent,
}
