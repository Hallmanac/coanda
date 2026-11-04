using System.Security.Cryptography;
using System.Text;
using Coanda.Core.Flows;
using Coanda.Core.Registry;
using Coanda.Daemon.Store;
using Fisher;
using JasperFx.Events.Documents;

namespace Coanda.Daemon.Flows;

/// <summary>
/// The session tokens the daemon issued to the judgments it launched. A token is 32 bytes from
/// the cryptographic random generator, so it cannot be computed from a flow, a launch, or another
/// token. Only its SHA-256 hash is kept, in the store's node tenant (<see cref="SessionTokenRecord"/>),
/// bound to the project, flow, and launch it was issued for; its plain value lives only in the
/// session's environment. A token resolves while the flow is running and its current visit still
/// expects that launch (<see cref="Flow.ExpectsLaunch"/>), and once that is false it stays false, so
/// the token never resolves again. Because the binding is in the store, a token resolves under the
/// same rule after a daemon restart, which is what lets the process of a reattached session reach
/// the daemon that reattached it.
/// </summary>
public sealed class SessionTokens(IDocumentStore store)
{
    private const int TokenBytes = 32;

    /// <summary>
    /// Issues a fresh token bound to <paramref name="binding"/>, stores its hash, and returns its
    /// plain value, which is kept nowhere here. The binding is saved before this returns, so a
    /// session started with the token afterwards can always resolve it.
    /// </summary>
    public async Task<string> IssueAsync(SessionBinding binding, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        await using IDocumentSession session = store.LightweightSession(Tenants.Node);
        session.Store(new SessionTokenRecord
        {
            Id = Hash(token),
            Project = binding.Project,
            FlowId = binding.FlowId,
            LaunchId = binding.LaunchId,
        });
        await session.SaveChangesAsync(cancellationToken);
        return token;
    }

    /// <summary>Removes every token issued for the launch <paramref name="launchId"/>, which then never resolves again.</summary>
    public async Task RevokeAsync(Guid launchId, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession(Tenants.Node);
        session.DeleteWhere<SessionTokenRecord>(r => r.LaunchId == launchId);
        await session.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Removes every stored token whose launch its flow no longer expects, such as one kept by a
    /// daemon stop whose flow was paused, stopped, or ended before the next start. Returns how many
    /// were removed.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<SessionTokenRecord> held;
        await using (IQuerySession query = store.QuerySession(Tenants.Node))
        {
            held = await query.Query<SessionTokenRecord>().ToListAsync(cancellationToken);
        }

        var spent = new List<Guid>();
        foreach (IGrouping<Guid, SessionTokenRecord> launch in held.GroupBy(r => r.LaunchId))
        {
            SessionTokenRecord any = launch.First();
            if (!await ExpectedAsync(new SessionBinding(any.Project, any.FlowId, any.LaunchId), cancellationToken))
            {
                spent.Add(launch.Key);
            }
        }

        foreach (Guid launchId in spent)
        {
            await RevokeAsync(launchId, cancellationToken);
        }

        return held.Count(r => spent.Contains(r.LaunchId));
    }

    /// <summary>
    /// The session principal <paramref name="presented"/> resolves to, or null when no daemon on this
    /// node issued it, or it was issued for a launch its flow no longer expects.
    /// </summary>
    public async ValueTask<Principal?> ResolveAsync(string presented, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return null;
        }

        SessionTokenRecord? record;
        await using (IQuerySession query = store.QuerySession(Tenants.Node))
        {
            record = await query.LoadAsync<SessionTokenRecord>(Hash(presented), cancellationToken);
        }

        if (record is null)
        {
            return null;
        }

        var binding = new SessionBinding(record.Project, record.FlowId, record.LaunchId);
        return await ExpectedAsync(binding, cancellationToken)
            ? Principal.ForSession(binding)
            : null;
    }

    private async Task<bool> ExpectedAsync(SessionBinding binding, CancellationToken cancellationToken)
    {
        await using IQuerySession session = store.QuerySession(binding.Project);
        Flow? flow = await session.LoadAsync<Flow>(binding.FlowId, cancellationToken);
        return flow is not null && flow.ExpectsLaunch(binding.LaunchId);
    }

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
