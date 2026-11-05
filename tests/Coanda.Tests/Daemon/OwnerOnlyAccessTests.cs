using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Coanda.Cli;
using Coanda.Cli.Commands;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Microsoft.Data.Sqlite;

namespace Coanda.Tests.Daemon;

/// <summary>
/// The node home's token file, database with its companion files, and log are readable and
/// writable by their owner alone: mode 0600 on macOS and Linux, and on Windows an access list whose
/// only entry grants the current user full control, inherited from the home's own owner-only list.
/// Every assertion checks whichever of the two the platform it runs on has.
/// </summary>
public sealed class OwnerOnlyAccessTests
{
    private const UnixFileMode Wider =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead;

    private const UnixFileMode WiderDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    [Fact]
    public async Task A_daemon_started_in_a_new_empty_home_keeps_every_file_owner_only()
    {
        using var scratch = new ScratchHome(create: false);
        NodeHome home = scratch.Home;

        await using RunningDaemon daemon = await Daemons.StartAsync(home, CancellationToken.None);
        await RegisteredProjects.AddAsync(daemon, "coanda", CancellationToken.None);
        await WriteToStoreAsync(home);

        AssertWhileRunning(home);
        await daemon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_daemon_started_over_files_an_earlier_run_left_wider_narrows_them_all()
    {
        using var scratch = new ScratchHome(create: true);
        NodeHome home = scratch.Home;
        SeedWriteAheadLogDatabase(home);
        await File.WriteAllTextAsync(home.DaemonInfoPath, "{}", CancellationToken.None);
        string[] seeded = [home.DaemonInfoPath, home.DatabasePath, Wal(home), Shm(home)];
        foreach (string path in seeded)
        {
            Widen(path);
            AssertWider(path);
        }

        await using RunningDaemon daemon = await Daemons.StartAsync(home, CancellationToken.None);
        await RegisteredProjects.AddAsync(daemon, "coanda", CancellationToken.None);
        await WriteToStoreAsync(home);

        AssertWhileRunning(home);
        await daemon.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void The_older_record_check_before_a_detached_start_narrows_the_database_before_opening_it()
    {
        using var scratch = new ScratchHome(create: true);
        NodeHome home = scratch.Home;
        using (SqliteConnection connection = Open(home.DatabasePath))
        {
            Execute(connection, "PRAGMA journal_mode=WAL; CREATE TABLE seed (x TEXT); INSERT INTO seed VALUES ('a');");
        }

        // a clean close removes the companion files
        Assert.DoesNotContain(home.DatabaseCompanionPaths, File.Exists);
        Widen(home.DatabasePath);
        AssertWider(home.DatabasePath);

        new DaemonClient(home).PrepareToStart();

        AssertOwnerOnly(home.DatabasePath);
        foreach (string companion in home.DatabaseCompanionPaths.Where(File.Exists))
        {
            AssertOwnerOnly(companion);
        }

        AssertHomeDirectory(home);
    }

    [Fact]
    public async Task The_daemon_log_is_created_owner_only()
    {
        using var scratch = new ScratchHome(create: true);
        NodeHome home = scratch.Home;

        await using (StreamWriter log = DaemonRunCommand.OpenLog(home))
        {
            await log.WriteLineAsync("started");
        }

        AssertOwnerOnly(home.LogPath);
        AssertHomeDirectory(home);
    }

    [Fact]
    public async Task A_daemon_log_an_earlier_run_left_wider_is_narrowed_and_appended_to()
    {
        using var scratch = new ScratchHome(create: true);
        NodeHome home = scratch.Home;
        await File.WriteAllTextAsync(home.LogPath, "earlier" + Environment.NewLine, CancellationToken.None);
        Widen(home.LogPath);
        AssertWider(home.LogPath);

        await using (StreamWriter log = DaemonRunCommand.OpenLog(home))
        {
            await log.WriteLineAsync("later");
        }

        AssertOwnerOnly(home.LogPath);
        AssertHomeDirectory(home);
        Assert.Equal("earlier" + Environment.NewLine + "later" + Environment.NewLine, await File.ReadAllTextAsync(home.LogPath, CancellationToken.None));
    }

    [Fact]
    public void The_narrowing_the_daemon_runs_at_start_narrows_every_directory_and_file_under_the_module_area()
    {
        using var scratch = new ScratchHome(create: true);
        NodeHome home = scratch.Home;
        home.EnsureExists();
        AssertOwnerOnlyDirectory(home.ModuleAreaPath);

        // A module's directory that already exists, as OwnerOnly.EnsureDirectory would leave it, a profile
        // directory inside it, and files at both depths, each written with wider access.
        string module = Path.Combine(home.ModuleAreaPath, "test");
        string profile = Path.Combine(module, "work");
        Directory.CreateDirectory(profile);
        string flat = Path.Combine(module, "personal");
        string token = Path.Combine(profile, "token");
        File.WriteAllText(flat, "secret");
        File.WriteAllText(token, "secret");
        string[] directories = [home.ModuleAreaPath, module, profile];
        string[] files = [flat, token];
        foreach (string path in directories)
        {
            WidenDirectory(path);
            AssertWiderDirectory(path);
        }

        foreach (string path in files)
        {
            WidenAnywhere(path);
            AssertWider(path);
        }

        home.EnsureExists();

        foreach (string path in directories)
        {
            AssertOwnerOnlyDirectory(path);
        }

        foreach (string path in files)
        {
            AssertOwnerOnly(path);
            if (OperatingSystem.IsWindows())
            {
                // a file under the module area holds its own list, not one inherited from its directory
                FileSecurity security = new FileInfo(path).GetAccessControl();
                Assert.True(security.AreAccessRulesProtected, $"{path}'s access list is not inherited from its parent");
                Assert.False(SingleRule(security, path).IsInherited);
            }
        }
    }

    [Fact]
    public void A_modules_own_area_is_created_owner_only_and_its_name_must_be_plain()
    {
        using var scratch = new ScratchHome(create: true);
        NodeHome home = scratch.Home;

        string area = home.EnsureModuleArea("test");

        Assert.Equal(Path.Combine(home.Path, "modules-private", "test"), area);
        AssertOwnerOnlyDirectory(home.ModuleAreaPath);
        AssertOwnerOnlyDirectory(area);
        Assert.Throws<Coanda.Core.DomainException>(() => home.ModuleAreaOf("../test"));
    }

    private static string Wal(NodeHome home) => home.DatabasePath + "-wal";

    private static string Shm(NodeHome home) => home.DatabasePath + "-shm";

    private static async Task WriteToStoreAsync(NodeHome home)
    {
        using var client = new DaemonClient(home);
        await client.PostAsync<ParcelView>(
            "/parcels", new ParcelAddRequest("coanda", "idea", "Written while running", null), CancellationToken.None);
    }

    /// <summary>SQLite deletes the companion files on a clean close, so they are read while the daemon runs.</summary>
    private static void AssertWhileRunning(NodeHome home)
    {
        Assert.True(File.Exists(Wal(home)), "the store runs in write-ahead-log mode");
        Assert.True(File.Exists(Shm(home)), "the store runs in write-ahead-log mode");
        foreach (string path in (string[])[home.DaemonInfoPath, home.DatabasePath, Wal(home), Shm(home)])
        {
            AssertOwnerOnly(path);
        }

        AssertHomeDirectory(home);
    }

    /// <summary>
    /// Copies a database in write-ahead-log mode, with its non-empty write-ahead log and shared-memory
    /// file, while a connection to it is still open with automatic checkpoints off, so SQLite reads
    /// and keeps the copies instead of recreating them.
    /// </summary>
    private static void SeedWriteAheadLogDatabase(NodeHome home)
    {
        using var source = new ScratchHome(create: true);
        string database = source.Home.DatabasePath;
        using SqliteConnection connection = Open(database);
        Execute(connection, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE seed (x TEXT); INSERT INTO seed VALUES ('a');");

        foreach (string suffix in (string[])["", "-wal", "-shm"])
        {
            using var from = new FileStream(database + suffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var to = new FileStream(home.DatabasePath + suffix, FileMode.CreateNew, FileAccess.Write);
            from.CopyTo(to);
        }

        long wal = new FileInfo(Wal(home)).Length;
        long shm = new FileInfo(Shm(home)).Length;
        Assert.True(wal > 0, $"Expected a write-ahead log with content, but its length is {wal}.");
        Assert.True(shm > 0, $"Expected a shared-memory file with content, but its length is {shm}.");
    }

    private static SqliteConnection Open(string database)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ConnectionString);
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// On macOS and Linux a mode the group and others can read. On Windows a file created in a
    /// directory that was not narrowed already carries the wider access list inherited from its parent.
    /// </summary>
    private static void Widen(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, Wider);
        }
    }

    /// <summary>
    /// Gives a file wider access whatever directory it is in: on macOS and Linux a mode the group and
    /// others can read, and on Windows an access list that inherits from its parent and grants every user read.
    /// </summary>
    private static void WidenAnywhere(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var file = new FileInfo(path);
            FileSecurity security = file.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Read, AccessControlType.Allow));
            file.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(path, Wider);
        }
    }

    /// <summary>On macOS and Linux mode 0755; on Windows an access list that inherits from its parent and grants every user read.</summary>
    private static void WidenDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var directory = new DirectoryInfo(path);
            DirectorySecurity security = directory.GetAccessControl();
            security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.Read,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(security);
        }
        else
        {
            File.SetUnixFileMode(path, WiderDirectory);
        }
    }

    private static void AssertWiderDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            int rules = new DirectoryInfo(path).GetAccessControl()
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Count;
            Assert.True(rules > 1, $"{path} is seeded with wider access, but holds {rules} access rule(s).");
        }
        else
        {
            // {path} is seeded with wider access
            Assert.Equal(WiderDirectory, File.GetUnixFileMode(path));
        }
    }

    /// <summary>On macOS and Linux mode 0700; on Windows an access list that is not inherited and whose only entry grants the current user full control.</summary>
    private static void AssertOwnerOnlyDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            DirectorySecurity security = new DirectoryInfo(path).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected, $"{path}'s access list is not inherited from its parent");
            Assert.False(SingleRule(security, path).IsInherited);
        }
        else
        {
            // {path} is owner-only
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(path));
        }
    }

    private static void AssertWider(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            int rules = new FileInfo(path).GetAccessControl()
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Count;
            Assert.True(rules > 1, $"{path} is seeded with wider access, but holds {rules} access rule(s).");
        }
        else
        {
            // {path} is seeded with wider access
            Assert.Equal(Wider, File.GetUnixFileMode(path));
        }
    }

    private static void AssertOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            FileSystemAccessRule rule = SingleRule(new FileInfo(path).GetAccessControl(), path);
            Assert.Equal(InheritanceFlags.None, rule.InheritanceFlags);
        }
        else
        {
            // {path} is owner-only
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    /// <summary>On Windows the home's own list is not inherited and is inherited by everything in it; elsewhere its mode is not changed.</summary>
    private static void AssertHomeDirectory(NodeHome home)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        DirectorySecurity security = new DirectoryInfo(home.Path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected, "the home's access list is not inherited from its parent");
        FileSystemAccessRule rule = SingleRule(security, home.Path);
        Assert.False(rule.IsInherited);
        Assert.Equal(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, rule.InheritanceFlags);
        Assert.Equal(PropagationFlags.None, rule.PropagationFlags);
    }

    [SupportedOSPlatform("windows")]
    private static FileSystemAccessRule SingleRule(FileSystemSecurity security, string path)
    {
        AuthorizationRuleCollection rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
        // {path} grants its owner alone
        Assert.Single(rules);
        FileSystemAccessRule rule = Assert.IsType<FileSystemAccessRule>(rules[0]);
        Assert.Equal(WindowsIdentity.GetCurrent().User, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
        return rule;
    }

    /// <summary>A throwaway home that, unlike <see cref="TemporaryHome"/>, is never narrowed by the test itself.</summary>
    private sealed class ScratchHome : IDisposable
    {
        public ScratchHome(bool create)
        {
            Home = new NodeHome(Path.Combine(Path.GetTempPath(), "coanda-tests", Guid.NewGuid().ToString("N")));
            if (create)
            {
                Directory.CreateDirectory(Home.Path);
            }
        }

        public NodeHome Home { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Home.Path, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
