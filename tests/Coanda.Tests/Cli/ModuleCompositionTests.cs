using Coanda.Cli;
using Coanda.Core.Definitions;
using Coanda.Core.Projects;
using Coanda.Core.Registry;
using Coanda.Daemon.Api;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Store;
using Coanda.Modules.Software;
using Coanda.Sdk.Client;
using Coanda.Sdk.Hosting;
using Coanda.Tests.Daemon;
using Coanda.Tests.Daemon.Commands;
using Coanda.Tests.Daemon.Modules;
using Coanda.Tests.Modules.Software.GitHub;
using Fisher;
using JasperFx.Events.Documents;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Cli;

/// <summary>
/// The executable composes the software module and hands it to the daemon (<see cref="ModuleComposition"/>):
/// a daemon started through that composition hosts the module, imports its definitions in module scope,
/// issues it a core token, and switches it on for a newly registered project, while a module handed to
/// the daemon already built gets a module area and no core token.
/// </summary>
public sealed class ModuleCompositionTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_daemon_started_through_the_composition_hosts_the_software_module_imports_its_definitions_in_module_scope_and_switches_it_on_for_a_new_project()
    {
        using var temp = new TemporaryHome();
        DaemonOptions options = ModuleComposition.Compose(
            new DaemonOptions { CommandRunner = new ScriptedCommandRunner() },
            new SoftwareHosting { GitHubHandler = new FakeGitHub().Handler });

        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, options, None);

        Assert.Equal([SoftwareSteps.Module], daemon.Services.GetRequiredService<HostedModules>().Names);
        Assert.IsType<SoftwareModule>(daemon.Services.GetRequiredService<SoftwareModule>());
        Assert.True(Directory.Exists(temp.Home.ModuleAreaOf(SoftwareSteps.Module)));

        // The module's core access was connected with a token that resolves to the module's own principal.
        CoreApiOptions core = daemon.Services.GetRequiredKeyedService<ModuleCoreAccess>(SoftwareSteps.Module).Options();
        Assert.Equal(new Uri(daemon.Info.BaseAddress), core.Address);
        Principal? module = await daemon.Services.GetRequiredService<ModuleTokens>().ResolveAsync(core.Token, None);
        Assert.Equal((Role.Module, SoftwareSteps.Module), (module?.Role, module?.Module));

        List<Definition> imported;
        await using (IQuerySession session = daemon.Services.GetRequiredService<IDocumentStore>().QuerySession(Tenants.Node))
        {
            imported = [.. await session.Query<Definition>().ToListAsync(None)];
        }

        Assert.Equal(SoftwareDefinitions.All.Count, imported.Count);
        Assert.All(imported, d => Assert.Equal((DefinitionScope.Module, SoftwareSteps.Module), (d.Scope, d.Module)));

        Project registered = await RegisteredProjects.AddAsync(daemon, "alpha", None);
        Assert.Equal([SoftwareSteps.Module], registered.Modules);
    }

    [Fact]
    public async Task A_module_handed_to_the_daemon_already_built_gets_a_module_area_and_no_core_token()
    {
        using var temp = new TemporaryHome();
        var options = new DaemonOptions { Modules = new Dictionary<string, IModule> { ["cms"] = new FeedModule([]) } };

        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, options, None);

        Assert.Equal(["cms", SoftwareSteps.Module], daemon.Services.GetRequiredService<HostedModules>().Names.Order(StringComparer.Ordinal));
        Assert.True(Directory.Exists(temp.Home.ModuleAreaOf("cms")));
        Assert.Null(daemon.Services.GetKeyedService<ModuleCoreAccess>("cms"));
        await using IQuerySession session = daemon.Services.GetRequiredService<IDocumentStore>().QuerySession(Tenants.Node);
        Assert.Equal([SoftwareSteps.Module], (await session.Query<ModuleTokenRecord>().ToListAsync(None)).Select(r => r.Module));
    }

    [Fact]
    public async Task Two_composed_modules_of_one_type_are_refused_before_the_daemon_listens()
    {
        using var temp = new TemporaryHome();
        var options = new DaemonOptions
        {
            ComposedModules =
            [
                ComposedModule.Of("other", ModuleComposition.SoftwareModulePath, _ => new FeedModule([])),
                ComposedModule.Of("cms", ModuleComposition.SoftwareModulePath, _ => new FeedModule([])),
            ],
        };

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => DaemonHost.StartAsync(temp.Home, 0, options, None));

        Assert.Equal("The module 'cms' is composed as a FeedModule, and another composed module already is; the daemon builds one module of each type.", refused.Message);
        Assert.False(File.Exists(temp.Home.DaemonInfoPath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_gates_command_limit_that_is_not_positive_is_refused_before_the_daemon_starts(int seconds) =>
        // coa daemon run composes its options before it starts the daemon, so the refusal comes first.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ModuleComposition.Compose(new DaemonOptions(), new SoftwareHosting { GatesCommandLimit = TimeSpan.FromSeconds(seconds) }));
}
