using System.Text;

namespace Coanda.Daemon.Executors;

/// <summary>
/// The inbox of a live agent session the executor did not start, as that session exports it: the
/// <see cref="Address"/> its inbox listens at and the <see cref="Token"/> a sender authenticates
/// with. The token is a secret: this type's printed form leaves it out, and no refusal names it.
/// </summary>
public sealed record SessionInbox
{
    public SessionInbox(string address, string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new ArgumentException($"The inbox at {address} was given no token.", nameof(token));
        }

        if (!Path.IsPathFullyQualified(address))
        {
            throw new ArgumentException($"The inbox address '{address}' is not an absolute path.", nameof(address));
        }

        Address = address;
        Token = token;
    }

    /// <summary>Where the inbox listens: the absolute path of its socket.</summary>
    public string Address { get; }

    /// <summary>The token the inbox's session exports for senders to authenticate with.</summary>
    public string Token { get; }

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Address = ").Append(Address).Append(", Token = (withheld)");
        return true;
    }
}
