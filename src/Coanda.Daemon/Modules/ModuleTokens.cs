using System.Security.Cryptography;
using System.Text;
using Coanda.Core.Registry;
using Coanda.Daemon.Store;
using Fisher;

namespace Coanda.Daemon.Modules;

/// <summary>
/// The module tokens the daemon issued. A token is 32 bytes from the cryptographic random generator,
/// as a session token is, and only its SHA-256 hash is kept, in the store's node tenant
/// (<see cref="ModuleTokenRecord"/>), bound to the module it was issued for. A token resolves to that
/// module's principal (<see cref="Principal.ForModule"/>) for as long as its record is held. Nothing
/// in the API or the CLI issues one: installing a module will, and until then only a caller in the
/// daemon's own process can, as the host of the in-process software module does at each start.
/// </summary>
public sealed class ModuleTokens(IDocumentStore store)
{
    private const int TokenBytes = 32;

    /// <summary>
    /// Issues a fresh token for the module <paramref name="module"/>, a module's name
    /// (<see cref="ModuleNames.IsModuleName"/>), stores its hash, and returns its plain value, which is
    /// kept nowhere here. Any other name is refused and nothing is stored.
    /// </summary>
    public async Task<string> IssueAsync(string module, CancellationToken cancellationToken)
    {
        string name = ModuleNames.Require(module);
        string token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

        await using IDocumentSession session = store.LightweightSession(Tenants.Node);
        session.Store(new ModuleTokenRecord { Id = Hash(token), Module = name });
        await session.SaveChangesAsync(cancellationToken);
        return token;
    }

    /// <summary>
    /// Issues a fresh token for the module <paramref name="module"/> as <see cref="IssueAsync"/> does,
    /// first forgetting every token issued for it before, so only the one returned resolves. The host of
    /// an in-process module calls it at each start, when the token an earlier start handed it is gone
    /// with that start's process.
    /// </summary>
    public async Task<string> ReissueAsync(string module, CancellationToken cancellationToken)
    {
        string name = ModuleNames.Require(module);
        await using (IDocumentSession session = store.LightweightSession(Tenants.Node))
        {
            session.DeleteWhere<ModuleTokenRecord>(r => r.Module == name);
            await session.SaveChangesAsync(cancellationToken);
        }

        return await IssueAsync(name, cancellationToken);
    }

    /// <summary>The module principal <paramref name="presented"/> resolves to, or null when no daemon on this node issued it.</summary>
    public async ValueTask<Principal?> ResolveAsync(string presented, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(presented))
        {
            return null;
        }

        await using IQuerySession query = store.QuerySession(Tenants.Node);
        ModuleTokenRecord? record = await query.LoadAsync<ModuleTokenRecord>(Hash(presented), cancellationToken);
        return record is null
            ? null
            : Principal.ForModule(record.Module);
    }

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
