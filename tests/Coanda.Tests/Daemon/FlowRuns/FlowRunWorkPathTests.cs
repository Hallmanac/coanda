using Coanda.Cli;
using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Tests.Modules.Software;

namespace Coanda.Tests.Daemon.FlowRuns;

/// <summary>
/// The core's API shows each flow run's work path, the one its dispatches carry, so a module that tidies
/// a flow run's workspace outside a dispatch reads it there rather than computing it.
/// </summary>
public sealed class FlowRunWorkPathTests
{
    private const string Plain = "alpha";
    private const string NotPlain = "team/alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_flow_runs_view_carries_its_work_path_and_none_for_a_project_whose_name_is_not_a_plain_directory_name()
    {
        await using SoftwareNode node = await SoftwareNode.StartAsync(projects: [Plain, NotPlain]);
        using var client = new DaemonClient(node.Home);
        FlowRun plain = await StartAsync(node, Plain);
        FlowRun notPlain = await StartAsync(node, NotPlain);

        FlowRunView shown = await FlowRunDriver.ShowAsync(client, Plain, plain.Id);
        List<FlowRunView> listed = await client.GetAsync<List<FlowRunView>>($"/flow-runs?project={Plain}", None);
        // the read succeeds for a project that gives its flow runs no work path
        FlowRunView none = await FlowRunDriver.ShowAsync(client, NotPlain, notPlain.Id);

        Assert.Equal(node.Home.FlowRunWorkPath(Plain, plain.Id), shown.WorkPath);
        Assert.Equal(node.Home.FlowRunWorkPath(Plain, plain.Id), Assert.Single(listed).WorkPath);
        Assert.Null(none.WorkPath);
        Assert.Equal(notPlain.Id, none.Id);
    }

    private static async Task<FlowRun> StartAsync(SoftwareNode node, string project)
    {
        Parcel parcel = await node.TaskAsync("Somewhere to work", project);
        return await node.FlowRuns.StartAsync(Principal.LocalOwner, project, parcel.Id, SoftwareNode.Steps.Name, None);
    }
}
