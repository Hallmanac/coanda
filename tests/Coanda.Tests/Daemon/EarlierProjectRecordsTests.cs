using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Modules;
using Coanda.Core.Modules.Events;
using Coanda.Core.Projects;
using Coanda.Core.Registry;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Store;
using Coanda.Modules.Software;
using Coanda.Sdk.Contract;
using Coanda.Tests.Daemon.Commands;
using Coanda.Tests.Sdk;
using Fisher;
using JasperFx.Events.Documents;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon;

/// <summary>
/// A node home main wrote at 1fed907, before repos and module settings, starts and reads normally: each
/// project's stored remote is its first repo, and its stored primary branch, build and test commands, and
/// required checks are the software module's settings for it, which module show shows and the steps use.
/// The fixture is that home's database as <c>sqlite3 .dump</c> wrote it, with two projects registered
/// through main's project service: alpha, at https://github.com/example/alpha.git on trunk, with the build
/// command <c>make build</c>, the test command <c>make test</c>, and the required checks
/// <c>build-test (ubuntu-latest)</c> and <c>lint</c>; and beta, at git@github.com:example/beta.git on main,
/// with no build or test command and no required checks.
/// </summary>
public sealed class EarlierProjectRecordsTests
{
    private const string Fixture = "tests/Coanda.Tests/Daemon/Fixtures/store-written-by-1fed907.sql";

    private static readonly CancellationToken None = CancellationToken.None;

    private static void Restore(NodeHome home)
    {
        var builder = new SqliteConnectionStringBuilder { DataSource = home.DatabasePath, Pooling = false };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using SqliteCommand restore = connection.CreateCommand();
        restore.CommandText = File.ReadAllText(RepositoryFiles.PathOf(Fixture));
        restore.ExecuteNonQuery();
    }

    private static async Task<IReadOnlyList<(string Name, string? Value)>> SettingsAsync(RunningDaemon daemon, string project)
    {
        ModuleSettingsShown shown = await daemon.Services.GetRequiredService<ModuleSettingsService>()
            .ShowAsync(Principal.LocalOwner, project, SoftwareSteps.Module, None);
        return [.. shown.Replicated.Select(s => (s.Name, s.Source == SettingSource.Set ? s.Value?.GetRawText() : null))];
    }

    private static async Task<int> SettingEventsAsync(IDocumentStore store, string project)
    {
        await using IQuerySession session = store.QuerySession(project);
        IReadOnlyList<ModuleSettings> settings = await session.Query<ModuleSettings>().ToListAsync(None);
        int events = 0;
        foreach (ModuleSettings recorded in settings)
        {
            events += (await session.Events.FetchStreamAsync(recorded.Id, token: None)).Count;
        }

        return events;
    }

    [Fact]
    public async Task Each_projects_remote_is_its_first_repo_and_its_software_half_is_the_modules_settings()
    {
        using var temp = new TemporaryHome();
        Restore(temp.Home);

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        ProjectService projects = daemon.Services.GetRequiredService<ProjectService>();

        Assert.Equal(
            [("alpha", [new ProjectRepo("alpha", "https://github.com/example/alpha.git")]), ("beta", [new ProjectRepo("beta", "git@github.com:example/beta.git")])],
            (await projects.ListAsync(Principal.LocalOwner, None)).Select(p => (p.Name, p.Repos)));
        Assert.Equal(
            [
                (SoftwareSettings.PrimaryBranch, """{"alpha":"trunk"}"""),
                (SoftwareSettings.Build, "\"make build\""),
                (SoftwareSettings.Test, "\"make test\""),
                (SoftwareSettings.RequiredChecks, """["build-test (ubuntu-latest)","lint"]"""),
            ],
            await SettingsAsync(daemon, "alpha"));
        // Beta named no build or test command and no check, so the defaults stand and the checks are unset.
        Assert.Equal(
            [(SoftwareSettings.PrimaryBranch, """{"beta":"main"}"""), (SoftwareSettings.Build, null), (SoftwareSettings.Test, null), (SoftwareSettings.RequiredChecks, null)],
            await SettingsAsync(daemon, "beta"));

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task The_software_steps_use_the_settings_carried_from_the_earlier_record()
    {
        using var temp = new TemporaryHome();
        Restore(temp.Home);
        var runner = new ScriptedCommandRunner();

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { CommandRunner = runner }, None);
        SoftwareModule module = daemon.Services.GetRequiredService<SoftwareModule>();
        Guid alphaTask = (await daemon.Services.GetRequiredService<Coanda.Daemon.Parcels.ParcelService>()
            .AddAsync(Principal.LocalOwner, "alpha", "task", "Gate me", null, None)).Id;
        Guid flowRunId = Ids.New();

        DispatchAnswer gates = await module.DispatchAsync(Request("alpha", alphaTask, flowRunId, SoftwareSteps.Gates, temp.Home), None);
        DispatchAnswer worktree = await module.DispatchAsync(Request("alpha", alphaTask, Ids.New(), SoftwareSteps.Worktree, temp.Home), None);

        Assert.IsType<DispatchAnswer.Done>(gates);
        Assert.Equal(["make build", "make test"], runner.ShellLines);
        Assert.IsType<DispatchAnswer.Done>(worktree);
        Assert.Contains("remote add origin https://github.com/example/alpha.git", runner.GitCalls);
        Assert.Contains("fetch --no-tags --quiet origin +refs/heads/trunk:refs/remotes/origin/trunk", runner.GitCalls);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_later_start_carries_nothing_again_and_a_setting_changed_since_is_kept()
    {
        using var temp = new TemporaryHome();
        Restore(temp.Home);
        await using (RunningDaemon first = await Daemons.StartAsync(temp.Home, None))
        {
            await first.Services.GetRequiredService<ModuleSettingsService>().SetAsync(
                Principal.LocalOwner, "alpha", SoftwareSteps.Module, SoftwareSettings.Test, JsonSerializer.SerializeToElement("make check"), None);
            await first.StopAsync(None);
        }

        int before;
        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            before = await SettingEventsAsync(store, "alpha") + await SettingEventsAsync(store, "beta");
        }

        await using RunningDaemon second = await Daemons.StartAsync(temp.Home, None);
        await second.StopAsync(None);

        await using IDocumentStore after = StoreConfiguration.Open(temp.Home.DatabasePath);
        Assert.Equal(before, await SettingEventsAsync(after, "alpha") + await SettingEventsAsync(after, "beta"));
        await using IQuerySession session = after.QuerySession("alpha");
        Assert.Equal("\"make check\"", Assert.Single(await session.Query<ModuleSettings>().ToListAsync(None)).ValueOf(SoftwareSettings.Test));
    }

    [Fact]
    public async Task A_value_main_recorded_in_the_modules_settings_gives_way_to_the_one_its_record_ran_with()
    {
        using var temp = new TemporaryHome();
        Restore(temp.Home);

        // Main's steps read the project's record and never the module's settings, which its settings command
        // could still set, its example a primary branch given as text.
        await using (IDocumentStore earlier = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            Guid settingsId = Ids.New();
            await using IDocumentSession session = earlier.LightweightSession("alpha");
            session.Events.StartStream<ModuleSettings>(
                settingsId,
                new ModuleSettingSet(settingsId, "alpha", SoftwareSteps.Module, SoftwareSettings.PrimaryBranch, "\"develop\"", "owner", DateTimeOffset.UtcNow),
                new ModuleSettingSet(settingsId, "alpha", SoftwareSteps.Module, SoftwareSettings.Build, "\"inert\"", "owner", DateTimeOffset.UtcNow));
            await session.SaveChangesAsync(None);

            // Beta's registration named no test command, so it ran the default, whatever its settings held.
            Guid betaId = Ids.New();
            await using IDocumentSession beta = earlier.LightweightSession("beta");
            beta.Events.StartStream<ModuleSettings>(
                betaId, new ModuleSettingSet(betaId, "beta", SoftwareSteps.Module, SoftwareSettings.Test, "\"inert\"", "owner", DateTimeOffset.UtcNow));
            await beta.SaveChangesAsync(None);
        }

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);

        Assert.Equal(
            [
                (SoftwareSettings.PrimaryBranch, """{"alpha":"trunk"}"""),
                (SoftwareSettings.Build, "\"make build\""),
                (SoftwareSettings.Test, "\"make test\""),
                (SoftwareSettings.RequiredChecks, """["build-test (ubuntu-latest)","lint"]"""),
            ],
            await SettingsAsync(daemon, "alpha"));
        Assert.Equal(
            [(SoftwareSettings.PrimaryBranch, """{"beta":"main"}"""), (SoftwareSettings.Build, null), (SoftwareSettings.Test, "\"dotnet test\""), (SoftwareSettings.RequiredChecks, null)],
            await SettingsAsync(daemon, "beta"));

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task The_project_documents_main_wrote_are_rewritten_with_their_repos_and_none_of_the_software_half()
    {
        using var temp = new TemporaryHome();
        Restore(temp.Home);
        await using (RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None))
        {
            await daemon.StopAsync(None);
        }

        var builder = new SqliteConnectionStringBuilder { DataSource = temp.Home.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false };
        using var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT data FROM fi_doc_project WHERE tenant_id = 'alpha'";
        using JsonDocument document = JsonDocument.Parse((string?)command.ExecuteScalar() ?? "{}");

        Assert.Equal("""[{"name":"alpha","remote":"https://github.com/example/alpha.git"}]""", document.RootElement.GetProperty("repos").GetRawText());
        foreach (string gone in new[] { "remote", "primaryBranch", "buildCommand", "testCommand", "requiredChecks" })
        {
            Assert.False(document.RootElement.TryGetProperty(gone, out _), $"the rewritten document holds no '{gone}'");
        }
    }

    private static DispatchRequest Request(string project, Guid parcelId, Guid flowRunId, string step, NodeHome home) =>
        new(project, parcelId, flowRunId, step, Visit: 1, Ids.New(), Resumed: false, home.FlowRunWorkPath(project, flowRunId), new Dictionary<string, JsonElement>());
}
