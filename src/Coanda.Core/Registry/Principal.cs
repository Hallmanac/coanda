namespace Coanda.Core.Registry;

/// <summary>Who is calling. Every command and query carries one; nothing runs anonymously.</summary>
public sealed record Principal(string Id, Role Role)
{
    public static readonly Principal LocalOwner = new("owner", Role.Owner);
}

public enum Role
{
    Owner,
    Member,
    Agent,
}
