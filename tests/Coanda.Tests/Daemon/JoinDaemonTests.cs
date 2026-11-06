using Coanda.Cli;
using Coanda.Core.Projects.Events;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Identity;
using Coanda.Daemon.Transport;
using Coanda.Sdk.Commands;
using Coanda.Tests.Daemon.Transport;
using Fisher;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon;

/// <summary>
/// Two throwaway homes, each with its own daemon, and a local bare repository standing in for the project's
/// remote: the owner registers the project, adds parcels, and vouches for the second home, which joins with the
/// owner's fingerprint through the CLI's client, lists the same parcels, and adds one the owner then lists too.
/// </summary>
public sealed class JoinDaemonTests
{
    private const string Alpha = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_second_home_joins_through_the_daemon_takes_the_owners_id_and_its_parcel_reaches_the_owner()
    {
        using var ownerTemp = new TemporaryHome();
        using var joiningTemp = new TemporaryHome();
        string remote = Path.Combine(ownerTemp.Home.Path, "remotes", Alpha, $"{RegisteredProjects.Repo}.git");
        Directory.CreateDirectory(remote);
        await LedgerNode.GitAsync(remote, "init", "--bare", "--quiet");

        // The owner's node names its owner by an id other than the account the tests run as.
        const string ownerId = "owner-of-alpha";
        Assert.NotEqual(ownerId, Environment.UserName);
        NodeOwnerRecord.Write(ownerTemp.Home, ownerId);
        var git = new DaemonOptions { CommandRunner = new ProcessCommandRunner() };

        await using RunningDaemon ownerDaemon = await Daemons.StartAsync(ownerTemp.Home, git, None);
        await using RunningDaemon joiningDaemon = await Daemons.StartAsync(joiningTemp.Home, git, None);
        using var ownerCli = new DaemonClient(ownerTemp.Home, null);
        using var joiningCli = new DaemonClient(joiningTemp.Home, null);

        await ownerCli.PostAsync<ProjectView>("/projects", new ProjectAddRequest(Alpha, remote, RegisteredProjects.Settings), None);
        foreach (string title in new[] { "First", "Second" })
        {
            await ownerCli.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Alpha, "task", title, null), None);
        }

        NodeView ownerNode = await ownerCli.GetAsync<NodeView>("/node", None);
        NodeView joiningNode = await joiningCli.GetAsync<NodeView>("/node", None);
        Assert.Equal(Environment.UserName, joiningCli.PrincipalId);

        NodeVouchResult vouched = await ownerCli.PostAsync<NodeVouchResult>(
            "/node/vouch", new NodeVouchRequest(Alpha, joiningNode.NodeId.ToString("D"), joiningNode.PublicKey), None);
        Assert.Equal((true, true, true), (vouched.Recorded, vouched.RootRecorded, vouched.Pushed));

        JoinResult joined = await joiningCli.PostAsync<JoinResult>("/join", new JoinRequest(remote, ownerNode.Fingerprint), None);
        Assert.Equal((Alpha, joiningNode.NodeId, joiningNode.PublicKey, true), (joined.Project, joined.NodeId, joined.PublicKey, joined.Trusted));
        Assert.Null(Assert.Single(joined.Origins).Fault);

        // The joined home's CLI now names the owner's id, and the daemon accepts it as its owner.
        Assert.Equal(ownerId, joiningCli.PrincipalId);
        Assert.Equal(
            Titles(await ownerCli.GetAsync<List<ParcelView>>($"/parcels?project={Alpha}", None)),
            Titles(await joiningCli.GetAsync<List<ParcelView>>($"/parcels?project={Alpha}", None)));
        ParcelView added = await joiningCli.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Alpha, "task", "From the joined home", null), None);
        Assert.Equal(ownerId, added.CreatedBy);

        LedgerPublication published = await joiningDaemon.Services.GetRequiredService<LedgerExport>().PublishAsync(Alpha, None);
        Assert.True(published.Published, string.Join("; ", published.Failures));
        LedgerImportReport imported = await ownerDaemon.Services.GetRequiredService<LedgerImport>().ImportAsync(Alpha, None);
        Assert.All(imported.Origins, o => Assert.Null(o.Fault));
        Assert.Contains(added.Id, (await ownerCli.GetAsync<List<ParcelView>>($"/parcels?project={Alpha}", None)).Select(p => p.Id));

        await joiningDaemon.StopAsync(None);
        await ownerDaemon.StopAsync(None);
    }

    [Fact]
    public async Task A_home_with_no_owner_id_record_records_the_id_that_registers_a_project_on_it()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var cli = new DaemonClient(temp.Home, null);
        Assert.Null(NodeOwnerRecord.Read(temp.Home));

        ProjectView added = await cli.PostAsync<ProjectView>("/projects", new ProjectAddRequest(Alpha, RegisteredProjects.Remote, RegisteredProjects.Settings), None);

        Assert.Equal(Environment.UserName, added.RegisteredBy);
        Assert.Equal(Environment.UserName, NodeOwnerRecord.Read(temp.Home));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_restarted_joined_home_whose_import_stopped_after_the_registration_switches_no_module_on()
    {
        await using LedgerNode owner = await LedgerNode.StartAsync(Alpha);
        await using LedgerNode joined = await LedgerNode.StartAsync();
        LedgerPublication published = await owner.Export.PublishAsync(Alpha, None);
        Assert.True(published.Published, string.Join("; ", published.Failures));
        await VouchTests.StoreFirstLineAsync(owner, joined);
        IReadOnlyList<IEvent> before = await joined.EventsAsync(Alpha);
        Assert.IsType<ProjectRegistered>(Assert.Single(before).Data);
        await joined.CloseStoreAsync();

        await using (RunningDaemon daemon = await Daemons.StartAsync(joined.Home, None))
        {
            await using IQuerySession session = daemon.Services.GetRequiredService<IDocumentStore>().QuerySession(Alpha);
            IReadOnlyList<IEvent> after = await session.Events.QueryEventsAsync(e => e.Sequence > 0, None);
            Assert.Single(after);
            Assert.DoesNotContain(after, e => e.Data is ProjectModulesSet);
            await daemon.StopAsync(None);
        }

        // An imported registration is not this node's own, so the start records no owner id for it.
        Assert.Null(NodeOwnerRecord.Read(joined.Home));
    }

    [Fact]
    public async Task A_home_that_registered_a_project_before_the_owner_id_record_existed_records_the_id_it_has_acted_as_at_start()
    {
        await using LedgerNode earlier = await LedgerNode.StartAsync(Alpha);
        Assert.Null(NodeOwnerRecord.Read(earlier.Home));
        await earlier.CloseStoreAsync();

        await using (RunningDaemon daemon = await Daemons.StartAsync(earlier.Home, None))
        {
            Assert.Equal(NodeOwner.OfAccount().Id, NodeOwnerRecord.Read(earlier.Home));
            NodeOwner owner = daemon.Services.GetRequiredService<NodeOwner>();
            Assert.Equal((NodeOwner.OfAccount().Id, true), (owner.Id, owner.Recorded));
            Assert.NotNull(owner.RefusalOf("someone-else"));
            await daemon.StopAsync(None);
        }
    }

    private static IReadOnlyList<string> Titles(IEnumerable<ParcelView> parcels) => [.. parcels.Select(p => p.Title).Order(StringComparer.Ordinal)];
}
