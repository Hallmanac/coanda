using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Coanda.Core.Flows;
using Coanda.Core.Registry;
using Fisher;

namespace Coanda.Daemon.Flows;

/// <summary>
/// The session tokens this daemon issued to the judgments it launched. A token is 32 bytes from
/// the cryptographic random generator, so it cannot be computed from a flow, a launch, or another
/// token. Only its SHA-256 hash is kept, in memory, keyed to the flow and launch it is bound to;
/// its plain value lives only in the session's environment. A token resolves while the flow is
/// running and its current visit still expects that launch (<see cref="Flow.ExpectsLaunch"/>), and
/// the first resolve that finds otherwise forgets it, so it never resolves again. Nothing is kept
/// across a restart: a daemon that starts again resolves none of the tokens an earlier one issued.
/// </summary>
public sealed class SessionTokens(IDocumentStore store)
{
    private const int TokenBytes = 32;

    private readonly ConcurrentDictionary<string, SessionBinding> issued = new(StringComparer.Ordinal);

    /// <summary>Issues a fresh token bound to <paramref name="binding"/> and returns its plain value, which is kept nowhere here.</summary>
    public string Issue(SessionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        issued[Hash(token)] = binding;
        return token;
    }

    /// <summary>Forgets every token issued for the launch <paramref name="launchId"/>, which then never resolves again.</summary>
    public void Revoke(Guid launchId)
    {
        foreach (KeyValuePair<string, SessionBinding> held in issued)
        {
            if (held.Value.LaunchId == launchId)
            {
                issued.TryRemove(held);
            }
        }
    }

    /// <summary>
    /// The session principal <paramref name="presented"/> resolves to, or null when this daemon did
    /// not issue it, or issued it for a launch its flow no longer expects, in which case it is
    /// forgotten.
    /// </summary>
    public async ValueTask<Principal?> ResolveAsync(string presented, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return null;
        }

        string hash = Hash(presented);
        if (!issued.TryGetValue(hash, out SessionBinding? binding))
        {
            return null;
        }

        await using IQuerySession session = store.QuerySession(binding.Project);
        Flow? flow = await session.LoadAsync<Flow>(binding.FlowId, cancellationToken);
        if (flow is not null && flow.ExpectsLaunch(binding.LaunchId))
        {
            return Principal.ForSession(binding);
        }

        issued.TryRemove(new KeyValuePair<string, SessionBinding>(hash, binding));
        return null;
    }

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
