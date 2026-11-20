using Coanda.Cli.Commands;
using Coanda.Core;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Modules.Software;
using Coanda.Tests.Daemon;
using Coanda.Tests.Daemon.Attention;
using Fisher;

namespace Coanda.Tests.Cli;

/// <summary>
/// <c>coa attention list</c>, run as the built executable against a daemon the test started: every
/// project's paused flow runs with no <c>--project</c>, whatever <c>COANDA_PROJECT</c> or the current
/// directory names, one project's with it, and a sentence when nothing is paused.
/// </summary>
public sealed class AttentionCommandTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task The_list_prints_every_projects_paused_flow_runs_unless_project_names_one()
    {
        using var temp = new TemporaryHome();
        AttentionListTests.Seeded seeded;
        await using (IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, SoftwareDefinitions.All))
        {
            seeded = await AttentionListTests.SeedAsync(store, new AttentionListTests.Clock(), withRunning: false);
        }

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        string directory = Directory.CreateDirectory(Path.Combine(temp.Home.Path, "alpha")).FullName;

        // With no --project, neither COANDA_PROJECT nor the directory's name narrows it.
        Ran all = await CoaProcess.RunAsync(temp.Home, "alpha", directory, None, "attention", "list");
        Assert.True(all.ExitCode == 0, all.Error);
        Assert.All(AttentionRendering.Columns, column => Assert.Contains(column, all.Output, StringComparison.Ordinal));
        string[] order = [Ids.Short(seeded.Early), Ids.Short(seeded.Middle), Ids.Short(seeded.Late)];
        Assert.Equal(order, order.OrderBy(id => all.Output.IndexOf(id, StringComparison.Ordinal)));
        Assert.All(order, id => Assert.Contains(id, all.Output, StringComparison.Ordinal));
        Assert.Contains("beta", all.Output, StringComparison.Ordinal);
        string early = Assert.Single(all.Output.Split('\n'), line => line.Contains(Ids.Short(seeded.Early), StringComparison.Ordinal));
        foreach (string field in new[] { "alpha", Ids.Short(seeded.EarlyParcel), "Early", "build", "Waiting on the API owner", "Resume it once they answer" })
        {
            Assert.Contains(field, early, StringComparison.Ordinal);
        }

        Ran beta = await CoaProcess.RunAsync(temp.Home, None, "attention", "list", "--project", "beta");
        Assert.True(beta.ExitCode == 0, beta.Error);
        Assert.Contains(Ids.Short(seeded.Middle), beta.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(Ids.Short(seeded.Early), beta.Output, StringComparison.Ordinal);
        Assert.DoesNotContain(Ids.Short(seeded.Late), beta.Output, StringComparison.Ordinal);

        Ran unregistered = await CoaProcess.RunAsync(temp.Home, None, "attention", "list", "--project", "gamma");
        Assert.Equal(2, unregistered.ExitCode);
        Assert.Contains("Project 'gamma' is not registered", unregistered.Output, StringComparison.Ordinal);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task With_nothing_paused_the_list_prints_a_sentence_saying_so()
    {
        using var temp = new TemporaryHome();
        await using (IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath, SoftwareDefinitions.All))
        {
            await RegisteredProjects.AddAsync(store, "alpha", None);
        }

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);

        Ran all = await CoaProcess.RunAsync(temp.Home, None, "attention", "list");
        Assert.Equal(0, all.ExitCode);
        Assert.Equal("No flow run this node started is paused.", all.Output.Trim());

        Ran alpha = await CoaProcess.RunAsync(temp.Home, None, "attention", "list", "--project", "alpha");
        Assert.Equal(0, alpha.ExitCode);
        Assert.Equal("No flow run this node started is paused in project 'alpha'.", alpha.Output.Trim());

        await daemon.StopAsync(None);
    }

    [Fact]
    public void A_row_carries_every_field_as_given_and_the_path_names_a_project_only_when_one_is_given()
    {
        var entry = new AttentionView(
            "alpha", Guid.Parse("0192a3b4-0000-7000-8000-00000000abcd"), Guid.Parse("0192a3b4-0000-7000-8000-00000000ef01"),
            "A [bracketed] title", "build", "review", "Waiting on [someone]", "coa flow-run resume", new DateTimeOffset(2026, 11, 20, 9, 10, 0, TimeSpan.Zero));

        string[] row = AttentionRendering.Row(entry);

        Assert.Equal(AttentionRendering.Columns.Length, row.Length);
        Assert.Equal(["alpha", Ids.Short(entry.FlowRunId), Ids.Short(entry.ParcelId), "A [bracketed] title", "build", "review", "Waiting on [someone]", "coa flow-run resume"], row[..8]);
        Assert.Equal("/attention", AttentionRendering.PathFor(null));
        Assert.Equal("/attention?project=a%20b", AttentionRendering.PathFor("a b"));
    }
}
