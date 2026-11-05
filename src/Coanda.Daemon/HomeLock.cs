using Coanda.Core;

namespace Coanda.Daemon;

/// <summary>
/// The node home's lock: <see cref="NodeHome.LockPath"/>, opened for writing and shared with nobody.
/// A daemon takes it before it touches the database and holds it until it stops, so a daemon still
/// starting holds it as surely as one that is serving; a record export or rebuild takes it for as
/// long as it runs, so neither runs beside a daemon, and no daemon starts while either runs. The
/// operating system releases it when the process holding it ends, however it ends. The file is created
/// owner-only and holds nothing.
/// </summary>
public sealed class HomeLock : IDisposable
{
    private readonly FileStream held;

    private HomeLock(FileStream held) => this.held = held;

    /// <summary>
    /// Takes the home's lock for <paramref name="purpose"/>, which a refusal names, or throws when a
    /// daemon or a record export or rebuild already holds it. The home must exist.
    /// </summary>
    public static HomeLock Take(NodeHome home, string purpose)
    {
        ArgumentNullException.ThrowIfNull(home);
        try
        {
            return new HomeLock(OwnerOnly.Open(home.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException) when (File.Exists(home.LockPath))
        {
            throw new DomainException(
                $"A daemon is running or starting on the node home {home.Path}, or a record export or rebuild is running on it, so {purpose} did not start. "
                + "Stop the daemon with coa daemon stop, or wait for the export or rebuild to finish, then run this again.");
        }
    }

    public void Dispose() => held.Dispose();
}
