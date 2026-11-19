using System.Net;
using System.Text.Json;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Parcels;
using Coanda.Modules.Software;
using Coanda.Sdk.Client;
using Coanda.Tests.Daemon.Modules;
using Fisher;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.FlowRuns;

/// <summary>
/// A project's flow runs listed across its parcels by flow hash, by agent hash, or by both, through
/// the loopback API as a person and as a module, and through the SDK's core client. The flow runs are
/// written before the daemon starts and left paused or stopped, so the daemon advances none of them.
/// </summary>
public sealed class FlowRunHashListTests
{
    private const string Project = "alpha";
    private const string Elsewhere = "beta";
    private const string FirstAgent = "agenthash1";
    private const string SecondAgent = "agenthash2";
    private static readonly Principal Owner = Principal.LocalOwner;
    private static readonly ModelSpec Model = new("anthropic", "claude-opus-5-5", "high");
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>
    /// The flow runs of <see cref="Project"/>, oldest first: on the first parcel a build that launched
    /// <see cref="FirstAgent"/>, stopped, then a merge, which has no agent step, paused; on the second
    /// parcel a build that launched <see cref="FirstAgent"/>, stopped, then a build that launched
    /// <see cref="SecondAgent"/>, paused. <see cref="Elsewhere"/> holds a build that launched
    /// <see cref="FirstAgent"/>, and is switched on for no module.
    /// </summary>
    private sealed record Seeded(Guid FirstParcel, Guid SecondParcel, Guid A, Guid B, Guid C, Guid D, Guid ElsewhereRun);

    private static async Task<Seeded> SeedAsync(NodeHome home)
    {
        await using IDocumentStore store = await SeededStore.OpenAsync(home.DatabasePath, SoftwareDefinitions.All);
        await RegisteredProjects.AddAsync(store, Project, None);
        await RegisteredProjects.AddAsync(store, Elsewhere, None);
        var parcels = new ParcelService(store, TimeProvider.System);
        var flowRuns = new FlowRunService(store, TimeProvider.System);

        Parcel first = await parcels.AddAsync(Owner, Project, "task", "First", null, None);
        Parcel second = await parcels.AddAsync(Owner, Project, "task", "Second", null, None);
        Parcel elsewhere = await parcels.AddAsync(Owner, Elsewhere, "task", "Elsewhere", null, None);

        FlowRun a = await LaunchedBuildAsync(flowRuns, Project, first.Id, FirstAgent);
        await flowRuns.StopAsync(Owner, Project, a.Id, "Make room for the merge.", None);
        FlowRun b = await flowRuns.StartAsync(Owner, Project, first.Id, "merge", None);
        await flowRuns.PauseAsync(Owner, Project, b.Id, "Held for the list.", "Resume it.", None);

        FlowRun c = await LaunchedBuildAsync(flowRuns, Project, second.Id, FirstAgent);
        await flowRuns.StopAsync(Owner, Project, c.Id, "Make room for the next build.", None);
        FlowRun d = await LaunchedBuildAsync(flowRuns, Project, second.Id, SecondAgent);

        FlowRun other = await LaunchedBuildAsync(flowRuns, Elsewhere, elsewhere.Id, FirstAgent);
        await ProjectServices.Software(store, TimeProvider.System).SetModulesAsync(Owner, Elsewhere, [], None);

        return new Seeded(first.Id, second.Id, a.Id, b.Id, c.Id, d.Id, other.Id);
    }

    /// <summary>Starts a build on the parcel, runs its worktree step, launches the build agent as <paramref name="agentHash"/>, and pauses it.</summary>
    private static async Task<FlowRun> LaunchedBuildAsync(FlowRunService flowRuns, string project, Guid parcelId, string agentHash)
    {
        FlowRun flowRun = await flowRuns.StartAsync(Owner, project, parcelId, "build", None);
        await flowRuns.RecordResultAsync(Owner, project, flowRun.Id, "worktree", SoftwareSteps.Worktree, Json("""{"verdict":"done","path":"/tmp/wt","reused":false}"""), None);
        await flowRuns.StartStepAsync(Owner, project, flowRun.Id, "build", None);
        await flowRuns.SpawnedAsync(Owner, project, flowRun.Id, "build", agentHash, Model, None);
        return await flowRuns.PauseAsync(Owner, project, flowRun.Id, "Held for the list.", "Resume it.", None);
    }

    private static JsonElement Json(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static async Task<IReadOnlyList<Guid>> ListedAsync(DaemonClient client, string query) =>
        [.. (await client.GetAsync<List<FlowRunView>>($"/flow-runs?project={Project}{query}", None)).Select(f => f.Id)];

    [Fact]
    public async Task A_flow_hash_lists_every_flow_run_of_the_project_under_that_flow_on_any_parcel_oldest_first()
    {
        using var temp = new TemporaryHome();
        Seeded seeded = await SeedAsync(temp.Home);
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        string build = SoftwareFlows.Build.Hash;
        string upper = build.ToUpperInvariant();

        Assert.Equal([seeded.A, seeded.C, seeded.D], await ListedAsync(client, $"&flowHash={build}"));
        Assert.Equal([seeded.B], await ListedAsync(client, $"&flowHash={SoftwareFlows.Merge.Hash}"));

        List<FlowRunView> views = await client.GetAsync<List<FlowRunView>>($"/flow-runs?project={Project}&flowHash={build}", None);
        Assert.All(views, v => Assert.Equal(build, v.FlowHash));

        // The comparison is ordinal: a hash differing only in case, or one no flow run carries, lists nothing.
        Assert.NotEqual(build, upper);
        Assert.Empty(await ListedAsync(client, $"&flowHash={upper}"));
        Assert.Empty(await ListedAsync(client, "&flowHash=0000000000000000"));

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_agent_hash_lists_every_flow_run_with_a_visit_that_launched_that_agent_and_no_other()
    {
        using var temp = new TemporaryHome();
        Seeded seeded = await SeedAsync(temp.Home);
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);

        Assert.Equal([seeded.A, seeded.C], await ListedAsync(client, $"&agentHash={FirstAgent}"));
        Assert.Equal([seeded.D], await ListedAsync(client, $"&agentHash={SecondAgent}"));
        Assert.Empty(await ListedAsync(client, $"&agentHash={FirstAgent.ToUpperInvariant()}"));
        Assert.Empty(await ListedAsync(client, "&agentHash=agenthash"));

        // Each listed flow run's own view shows the visit the match was made on.
        List<FlowRunView> views = await client.GetAsync<List<FlowRunView>>($"/flow-runs?project={Project}&agentHash={SecondAgent}", None);
        Assert.Contains(Assert.Single(views).Steps, s => s.AgentHash == SecondAgent);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task Filters_given_together_list_only_the_flow_runs_meeting_every_one_and_none_given_lists_as_before()
    {
        using var temp = new TemporaryHome();
        Seeded seeded = await SeedAsync(temp.Home);
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        string build = SoftwareFlows.Build.Hash;
        string merge = SoftwareFlows.Merge.Hash;

        Assert.Equal([seeded.D], await ListedAsync(client, $"&flowHash={build}&agentHash={SecondAgent}"));
        Assert.Empty(await ListedAsync(client, $"&flowHash={merge}&agentHash={FirstAgent}"));
        Assert.Equal([seeded.C], await ListedAsync(client, $"&parcel={seeded.SecondParcel}&agentHash={FirstAgent}"));
        Assert.Equal([seeded.A], await ListedAsync(client, $"&parcel={seeded.FirstParcel}&flowHash={build}"));
        Assert.Equal([seeded.C], await ListedAsync(client, $"&parcel={seeded.SecondParcel}&flowHash={build}&agentHash={FirstAgent}"));

        Assert.Equal([seeded.A, seeded.B, seeded.C, seeded.D], await ListedAsync(client, string.Empty));
        Assert.Equal([seeded.A, seeded.B], await ListedAsync(client, $"&parcel={seeded.FirstParcel}"));
        Assert.Equal([seeded.C, seeded.D], await ListedAsync(client, $"&parcel={seeded.SecondParcel}"));

        await daemon.StopAsync(None);
    }

    [Theory]
    [InlineData("flowHash", "", "flow")]
    [InlineData("flowHash", "%20", "flow")]
    [InlineData("agentHash", "", "agent")]
    [InlineData("agentHash", "%20%09", "agent")]
    public async Task A_hash_given_empty_or_blank_is_refused_with_a_reason(string parameter, string value, string kind)
    {
        using var temp = new TemporaryHome();
        await SeedAsync(temp.Home);
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);

        DomainException refused = await Assert.ThrowsAsync<DomainException>(
            () => client.GetAsync<List<FlowRunView>>($"/flow-runs?project={Project}&{parameter}={value}", None));
        Assert.Equal(FlowRunService.BlankHashFilter(kind), refused.Message);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_modules_token_lists_as_a_person_does_where_its_module_is_switched_on_and_is_refused_elsewhere()
    {
        using var temp = new TemporaryHome();
        Seeded seeded = await SeedAsync(temp.Home);
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var person = new DaemonClient(temp.Home);
        string token = await daemon.Services.GetRequiredService<ModuleTokens>().MintAsync(SoftwareSteps.Module, None);
        using var http = new HttpClient { BaseAddress = new Uri(daemon.Info.BaseAddress) };
        string build = SoftwareFlows.Build.Hash;

        foreach (string query in new[] { $"&flowHash={build}", $"&agentHash={FirstAgent}", $"&flowHash={build}&agentHash={SecondAgent}" })
        {
            (HttpStatusCode status, string body) = await SendAsync(http, token, $"/flow-runs?project={Project}{query}");
            Assert.Equal(HttpStatusCode.OK, status);
            List<FlowRunView>? asModule = JsonSerializer.Deserialize<List<FlowRunView>>(body, JsonSerializerOptions.Web);
            Assert.NotNull(asModule);
            Assert.Equal(await ListedAsync(person, query), asModule.Select(f => f.Id));
        }

        (HttpStatusCode unfilteredStatus, string unfiltered) = await SendAsync(http, token, $"/flow-runs?project={Elsewhere}");
        (HttpStatusCode filteredStatus, string filtered) = await SendAsync(http, token, $"/flow-runs?project={Elsewhere}&flowHash={build}&agentHash={FirstAgent}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, unfilteredStatus);
        Assert.Equal(unfilteredStatus, filteredStatus);
        Assert.Equal(unfiltered, filtered);
        Assert.Equal(ModuleNode.NotSwitchedOn(SoftwareSteps.Module, Elsewhere), JsonSerializer.Deserialize<ErrorView>(filtered, JsonSerializerOptions.Web)?.Error);

        // A person still sees the other project's flow run, so the refusal is the module's scope and not an empty project.
        List<FlowRunView> elsewhere = await person.GetAsync<List<FlowRunView>>($"/flow-runs?project={Elsewhere}&agentHash={FirstAgent}", None);
        Assert.Equal([seeded.ElsewhereRun], elsewhere.Select(f => f.Id));

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task The_sdks_core_client_lists_a_projects_flow_runs_by_hash_with_the_hashes_on_each_record()
    {
        using var temp = new TemporaryHome();
        Seeded seeded = await SeedAsync(temp.Home);
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        string token = await daemon.Services.GetRequiredService<ModuleTokens>().MintAsync(SoftwareSteps.Module, None);
        using var http = new HttpClient();
        var core = new CoreApiClient(http, new CoreApiOptions(new Uri(daemon.Info.BaseAddress), token));
        string build = SoftwareFlows.Build.Hash;

        IReadOnlyList<CoreFlowRun> all = await core.ProjectFlowRunsAsync(Project, null, null, None);
        Assert.Equal([seeded.A, seeded.B, seeded.C, seeded.D], all.Select(f => f.Id));

        IReadOnlyList<CoreFlowRun> byFlow = await core.ProjectFlowRunsAsync(Project, build, null, None);
        Assert.Equal([seeded.A, seeded.C, seeded.D], byFlow.Select(f => f.Id));
        Assert.All(byFlow, f => Assert.Equal(build, f.FlowHash));

        IReadOnlyList<CoreFlowRun> byAgent = await core.ProjectFlowRunsAsync(Project, null, FirstAgent, None);
        Assert.Equal([seeded.A, seeded.C], byAgent.Select(f => f.Id));
        Assert.All(byAgent, f => Assert.Equal(FirstAgent, f.Steps.Single(s => s.Step == "build").AgentHash));
        Assert.All(byAgent, f => Assert.Null(f.Steps.Single(s => s.Step == "worktree").AgentHash));

        IReadOnlyList<CoreFlowRun> byBoth = await core.ProjectFlowRunsAsync(Project, build, SecondAgent, None);
        Assert.Equal([seeded.D], byBoth.Select(f => f.Id));

        // The call that lists one parcel's flow runs answers as it did, and now carries the hashes too.
        IReadOnlyList<CoreFlowRun> onParcel = await core.FlowRunsAsync(Project, seeded.FirstParcel, None);
        Assert.Equal([seeded.A, seeded.B], onParcel.Select(f => f.Id));
        Assert.Equal([build, SoftwareFlows.Merge.Hash], onParcel.Select(f => f.FlowHash));

        CoreApiException blank = await Assert.ThrowsAsync<CoreApiException>(() => core.ProjectFlowRunsAsync(Project, " ", null, None));
        Assert.Equal(FlowRunService.BlankHashFilter("flow"), blank.Message);
        CoreApiException refused = await Assert.ThrowsAsync<CoreApiException>(() => core.ProjectFlowRunsAsync(Elsewhere, build, null, None));
        Assert.Equal(ModuleNode.NotSwitchedOn(SoftwareSteps.Module, Elsewhere), refused.Message);

        await daemon.StopAsync(None);
    }

    private static async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpClient http, string token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(DaemonHost.TokenHeader, token);
        using HttpResponseMessage response = await http.SendAsync(request, None);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(None));
    }
}
