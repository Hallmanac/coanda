using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Projects;
using Coanda.Core.Projects.Events;
using Coanda.Core.Registry;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Store;
using Coanda.Modules.Software;
using Coanda.Sdk.Hosting;
using Fisher;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Modules;

/// <summary>
/// The node configuration's <c>defaultModules</c> names the modules a project registered on the node is
/// switched on for, and those a project registered before modules were has switched on at start; a node
/// whose file names none switches on the software module alone, and a list naming a module the daemon
/// does not host stops the start before anything is recorded.
/// </summary>
public sealed class ConfiguredDefaultModulesTests
{
    private const string StandIn = "cms";
    private static readonly CancellationToken None = CancellationToken.None;

    [Theory]
    [InlineData(null, new[] { SoftwareSteps.Module })]
    [InlineData("""{ "executor": "scripted" }""", new[] { SoftwareSteps.Module })]
    [InlineData("""{ "executor": "scripted", "defaultModules": ["cms"] }""", new[] { StandIn })]
    [InlineData("""{ "executor": "scripted", "defaultModules": ["software", "cms"] }""", new[] { StandIn, SoftwareSteps.Module })]
    [InlineData("""{ "executor": "scripted", "defaultModules": [] }""", new string[0])]
    public async Task A_project_registered_through_the_api_is_switched_on_for_exactly_the_configured_modules(string? configuration, string[] expected)
    {
        using var temp = new TemporaryHome();
        Configure(temp, configuration);
        await using RunningDaemon daemon = await StartAsync(temp);
        using var client = new DaemonClient(temp.Home);

        await client.PostAsync<ProjectView>("/projects", new ProjectAddRequest("alpha", RegisteredProjects.Remote), None);

        Project? registered = await daemon.Services.GetRequiredService<ProjectService>().ShowAsync(Principal.LocalOwner, "alpha", None);
        Assert.Equal(expected, registered?.Modules);
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_registration_on_a_node_that_switches_on_no_module_refuses_a_setting_given_with_coa_project_add()
    {
        using var temp = new TemporaryHome();
        Configure(temp, """{ "executor": "scripted", "defaultModules": [] }""");
        await using RunningDaemon daemon = await StartAsync(temp);
        using var client = new DaemonClient(temp.Home);
        ProjectAddRequest request = new Coanda.Cli.Commands.ProjectAddSettings { Name = "alpha", Repo = RegisteredProjects.Remote, Settings = ["software/build=make"] }.ToRequest();

        DomainException refused = await Assert.ThrowsAsync<DomainException>(() => client.PostAsync<ProjectView>("/projects", request, None));

        Assert.Equal(
            "Project 'alpha' was given a setting of module 'software', which its registration does not switch on; a registration records the settings of the modules it switches on, and this one switches on no module.",
            refused.Message);
        Assert.Empty(await daemon.Services.GetRequiredService<ProjectService>().ListAsync(Principal.LocalOwner, None));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_project_registered_before_modules_were_has_the_configured_modules_switched_on_at_start_and_one_whose_modules_were_named_keeps_them()
    {
        using var temp = new TemporaryHome();
        Guid older;
        Guid named;
        await using (IDocumentStore store = await SeededStore.OpenAsync(temp.Home.EnsureExists().EnsureDatabase().DatabasePath))
        {
            older = await RegisterAsync(store, "older");
            named = await RegisterAsync(store, "named", new ProjectModulesSet(Guid.Empty, "named", [SoftwareSteps.Module], "owner", DateTimeOffset.UnixEpoch));
        }

        Configure(temp, """{ "executor": "scripted", "defaultModules": ["cms"] }""");
        await using RunningDaemon daemon = await StartAsync(temp);
        ProjectService projects = daemon.Services.GetRequiredService<ProjectService>();
        IDocumentStore started = daemon.Services.GetRequiredService<IDocumentStore>();

        Assert.Equal([StandIn], (await projects.ShowAsync(Principal.LocalOwner, "older", None))?.Modules);
        ProjectModulesSet switchedOn = Assert.Single(await EventsAsync<ProjectModulesSet>(started, "older", older));
        Assert.Equal(ProjectService.UpgradePrincipal.Id, switchedOn.SetBy);
        Assert.Equal([SoftwareSteps.Module], (await projects.ShowAsync(Principal.LocalOwner, "named", None))?.Modules);
        Assert.Single(await EventsAsync<ProjectModulesSet>(started, "named", named));
        await daemon.StopAsync(None);
    }

    [Theory]
    [InlineData(new[] { StandIn }, "it hosts 'software'")]
    [InlineData(new[] { SoftwareSteps.Module, "mail" }, "it hosts 'software'")]
    public async Task A_default_module_the_daemon_does_not_host_stops_the_start_naming_the_file_the_module_and_the_hosted_modules(string[] defaults, string hosts)
    {
        using var temp = new TemporaryHome();
        var options = new DaemonOptions { DefaultModules = defaults };

        DomainException refused = await Assert.ThrowsAsync<DomainException>(() => Daemons.StartAsync(temp.Home, options, None));

        string missing = defaults.First(d => d != SoftwareSteps.Module);
        Assert.Equal(
            $"The node configuration {temp.Home.ConfigurationPath} names the module '{missing}' in \"defaultModules\", which this daemon does not host; {hosts}.",
            refused.Message);
        Assert.False(File.Exists(temp.Home.DaemonInfoPath));
    }

    [Fact]
    public async Task A_daemon_hosting_no_module_says_so_when_it_refuses_a_default_module()
    {
        using var temp = new TemporaryHome();

        DomainException refused = await Assert.ThrowsAsync<DomainException>(
            () => DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { DefaultModules = [SoftwareSteps.Module] }, None));

        Assert.EndsWith("names the module 'software' in \"defaultModules\", which this daemon does not host; it hosts no module.", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "executor": "scripted", "defaultModules": ["mail"] }""", "names the module 'mail' in \"defaultModules\", which this daemon does not host; it hosts 'cms', 'software'.")]
    [InlineData("""{ "executor": "scripted", "defaultModules": ["cms", "cms"] }""", "names the module 'cms' more than once in \"defaultModules\"; entry 2 repeats it.")]
    [InlineData("""{ "executor": "scripted", "defaultModules": null }""", "gives \"defaultModules\" as null")]
    [InlineData("""{ "executor": "scripted", "defaultModules": [null] }""", "gives entry 1 of \"defaultModules\" as null")]
    public async Task A_refused_start_leaves_a_project_registered_before_modules_were_as_it_found_it(string configuration, string rule)
    {
        using var temp = new TemporaryHome();
        Guid older;
        await using (IDocumentStore store = await SeededStore.OpenAsync(temp.Home.EnsureExists().EnsureDatabase().DatabasePath))
        {
            older = await RegisterAsync(store, "older");
        }

        Configure(temp, configuration);
        DomainException refused = await Assert.ThrowsAsync<DomainException>(() => StartAsync(temp));

        Assert.StartsWith($"The node configuration {temp.Home.ConfigurationPath} ", refused.Message, StringComparison.Ordinal);
        Assert.Contains(rule, refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(temp.Home.DaemonInfoPath));
        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            await using IQuerySession session = store.QuerySession("older");
            IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(older, token: None);
            Assert.Equal([typeof(ProjectRegistered)], events.Select(e => e.Data.GetType()));
        }
    }

    /// <summary>Starts the daemon as <c>coa daemon run</c> does, from the node configuration, with a stand-in module hosted beside the software module.</summary>
    private static Task<RunningDaemon> StartAsync(TemporaryHome temp) =>
        Daemons.StartAsync(
            temp.Home,
            NodeConfiguration.Load(temp.Home) with { Modules = new Dictionary<string, IModule> { [StandIn] = new FeedModule([]) } },
            None);

    private static void Configure(TemporaryHome temp, string? configuration)
    {
        if (configuration is not null)
        {
            File.WriteAllText(temp.Home.EnsureExists().ConfigurationPath, configuration);
        }
    }

    private static async Task<Guid> RegisterAsync(IDocumentStore store, string name, params ProjectModulesSet[] after)
    {
        ProjectRegistered registered = ProjectDecider.Register(name, RegisteredProjects.Remote, null, "owner", DateTimeOffset.UnixEpoch);
        await using IDocumentSession session = store.LightweightSession(name);
        session.Events.StartStream<Project>(registered.ProjectId, [registered, .. after.Select(a => a with { ProjectId = registered.ProjectId })]);
        await session.SaveChangesAsync(None);
        return registered.ProjectId;
    }

    private static async Task<IReadOnlyList<T>> EventsAsync<T>(IDocumentStore store, string tenant, Guid streamId)
    {
        await using IQuerySession session = store.QuerySession(tenant);
        IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(streamId, token: None);
        return [.. events.Select(e => e.Data).OfType<T>()];
    }
}
