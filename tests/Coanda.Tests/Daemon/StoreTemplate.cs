using Coanda.Daemon;
using Coanda.Daemon.Store;
using Fisher;

namespace Coanda.Tests.Daemon;

/// <summary>
/// A record holding the store's schema and nothing else, made once per test run, which
/// <see cref="Daemons.StartAsync(NodeHome, CancellationToken)"/> copies into a home that has no record yet,
/// so the start finds the tables it would otherwise create.
/// </summary>
/// <remarks>
/// <para>
/// A daemon's start applies the store's whole schema (<see cref="StoreConfiguration.AddCoandaStore"/>). On a
/// new record that runs while the file is still in SQLite's rollback-journal mode, before the store's first
/// connection switches it to write-ahead logging, so each change is its own transaction that creates,
/// syncs, and deletes a journal file. Those are most of the files a test daemon's start and stop creates and
/// deletes, and most of the syncs it makes.
/// </para>
/// <para>
/// The template holds the tables that schema apply creates and no row. It is made through the same store
/// configuration the daemon uses, and the daemon still applies the whole schema at every start, so any table
/// the template lacks is created then, as before. A test whose subject is what a start does to a home with no
/// record starts through <see cref="Daemons.StartOnNewHomeAsync(NodeHome, CancellationToken)"/> instead.
/// </para>
/// </remarks>
public static class StoreTemplate
{
    private static readonly Lazy<Task<string>> Made = new(MakeAsync, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Copies the template into <paramref name="home"/> when its directory exists and holds no record, neither
    /// the database nor any of its companion files. A home the test has already given a record keeps it.
    /// </summary>
    /// <remarks>
    /// The copy is made beside the record and moved into place, so a start never opens a record half copied,
    /// and a record another start placed first is kept, leaving the home's lock to refuse whichever start
    /// comes second.
    /// </remarks>
    public static async Task CopyIntoAsync(NodeHome home, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(home.Path) || ((IEnumerable<string>)[home.DatabasePath, .. home.DatabaseCompanionPaths]).Any(File.Exists))
        {
            return;
        }

        string template = await Made.Value.WaitAsync(cancellationToken);
        string copy = $"{home.DatabasePath}.{Path.GetRandomFileName()}";
        File.Copy(template, copy);
        try
        {
            File.Move(copy, home.DatabasePath);
        }
        catch (IOException) when (File.Exists(home.DatabasePath))
        {
            File.Delete(copy);
        }
    }

    private static async Task<string> MakeAsync()
    {
        var temp = new TemporaryHome();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => temp.Dispose();
        NodeHome home = temp.Home.EnsureDatabase();
        await using (IDocumentStore store = StoreConfiguration.Open(home.DatabasePath))
        {
            // The template is made once for the whole run and shared by every test, so no one test's token may cancel it.
            await ((DocumentStore)store).ApplyAllConfiguredChangesToDatabaseAsync(CancellationToken.None);
        }

        if (home.DatabaseCompanionPaths.FirstOrDefault(File.Exists) is { } left)
        {
            throw new InvalidOperationException($"The store template's store closed and left {left} beside the record, so the record alone is not the whole template.");
        }

        return home.DatabasePath;
    }
}
