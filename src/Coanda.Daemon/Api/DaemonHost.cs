using System.Reflection;
using System.Security.Cryptography;
using Coanda.Core;
using Coanda.Core.Parcels;
using Coanda.Daemon.SystemSteps;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Api.Middleware;
using Coanda.Sdk.Commands;
using Coanda.Daemon.Flows;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Executors;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Store;
using Coanda.Daemon.Triggers;
using Coanda.Modules.Software;
using Coanda.Modules.Software.GitHub;
using Fisher;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Coanda.Daemon.Api;

/// <summary>
/// The daemon: the only writer to the node's record, hosting one loopback API that every surface
/// calls. Binds an ephemeral port on 127.0.0.1, writes its address and token to the node home,
/// and removes them on a clean stop.
/// </summary>
public sealed class DaemonHost
{
    public const string TokenHeader = "X-Coanda-Token";
    public const string PrincipalHeader = "X-Coanda-Principal";

    public static string Version =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    public static Task<RunningDaemon> StartAsync(NodeHome home, int port, CancellationToken cancellationToken) =>
        StartAsync(home, port, new DaemonOptions(), cancellationToken);

    public static async Task<RunningDaemon> StartAsync(NodeHome home, int port, DaemonOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.AgentStepLimit, TimeSpan.Zero, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.GatesCommandLimit, TimeSpan.Zero, nameof(options));

        // Narrows what an earlier run left wider, then takes the home's lock before anything touches the
        // database, holding it until the daemon stops, so a record export or rebuild never runs beside a
        // daemon, even one still starting.
        home.EnsureExists();
        HomeLock homeLock = HomeLock.Take(home, "the daemon");
        try
        {
            return await StartLockedAsync(home, port, options, homeLock, cancellationToken);
        }
        catch
        {
            homeLock.Dispose();
            throw;
        }
    }

    private static async Task<RunningDaemon> StartLockedAsync(NodeHome home, int port, DaemonOptions options, HomeLock homeLock, CancellationToken cancellationToken)
    {
        // The home was narrowed before the lock was taken; the database is created owner-only before the
        // check below or the store opens it.
        home.EnsureDatabase();
        // Made at the first start on a home and read at every later one; a home holding part of one is refused here.
        NodeIdentity identity = await NodeIdentity.EnsureAsync(home, cancellationToken);
        string[] moduleAreas = [SoftwareSteps.Module, .. options.Modules.Keys, .. options.ModuleSettingsSchemas?.Modules ?? []];
        foreach (string module in moduleAreas)
        {
            home.EnsureModuleArea(module);
        }

        // Before the store is configured, which would add this build's tables to an old record.
        PreRenameStore.ThrowIfFound(home);
        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ");
        if (options.LoggerProvider is { } provider)
        {
            builder.Logging.AddProvider(provider);
        }

        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddSingleton(home);
        builder.Services.AddSingleton(identity);
        builder.Services.AddSingleton(NodeOwner.OfAccount());
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddCoandaStore(home);
        // The shipped gates run whatever the options name; the options add gates, never remove one.
        builder.Services.AddSingleton(new GateSet([.. GateSet.ShippedGates, .. options.Gates]));
        // Flows are validated against the core's system steps and the steps the modules switched on for
        // the flow's project describe, or every hosted module's for a definition of no project, and a
        // module step's arguments against the argument schema its module describes.
        builder.Services.AddSingleton(services => new DefinitionService(
            services.GetRequiredService<IDocumentStore>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<GateSet>(),
            services.GetRequiredService<FlowSystemSteps>().NamesAsync,
            services.GetRequiredService<FlowSystemSteps>().ArgumentCheckAsync));
        builder.Services.AddSingleton(new CredentialProfiles(options.Profiles));
        builder.Services.AddSingleton<RepoClones>();
        builder.Services.AddSingleton<ProjectService>();
        builder.Services.AddSingleton<EarlierProjectRecords>();
        builder.Services.AddSingleton<ParcelService>();
        builder.Services.AddSingleton<FlowService>();
        builder.Services.AddSingleton<IAgentExecutor>(options.Executor ?? new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>()));
        builder.Services.AddSingleton(new FlowRunAdvancerOptions(options.AgentStepLimit));
        builder.Services.AddSingleton<ICommandRunner>(options.CommandRunner ?? new NoCommandRunner());
        builder.Services.AddSingleton<FlowRunWorkers>();

        // The software module, hosted in this process behind its host adapter. It is given the node
        // home, where it finds each repo's clone by the layout, its own area, the command runner, the
        // GitHub handler, the gates command limit, and the core's address and its token, which exist only
        // once the daemon is listening, and it reaches the daemon otherwise only through the core's API.
        var softwareCore = new ModuleCoreAccess(SoftwareSteps.Module);
        builder.Services.AddSingleton(softwareCore);
        builder.Services.AddSingleton(services => new SoftwareModule(
            new SoftwareModuleOptions
            {
                HomePath = home.Path,
                ProfilesPath = home.ModuleAreaOf(SoftwareSteps.Module),
                CoreApi = softwareCore.Options,
                CommandRunner = services.GetRequiredService<ICommandRunner>(),
                GitHubHandler = options.GitHubHandler ?? (() => new NoGitHubHandler()),
                LoggerFactory = services.GetRequiredService<ILoggerFactory>(),
                GatesCommandLimit = options.GatesCommandLimit,
            }));
        builder.Services.AddSingleton(services => new HostedModules(
            new Dictionary<string, ModuleHostAdapter>(
                [
                    new(SoftwareSteps.Module, new ModuleHostAdapter(services.GetRequiredService<SoftwareModule>())),
                    .. options.Modules.Select(m => new KeyValuePair<string, ModuleHostAdapter>(m.Key, new ModuleHostAdapter(m.Value))),
                ],
                StringComparer.Ordinal),
            options.ModuleSettingsSchemas));
        builder.Services.AddSingleton(services => new FlowSystemSteps(
            [
                .. FlowSystemSteps.Core(services.GetRequiredService<ParcelService>(), services.GetRequiredService<TimeProvider>()),
                .. options.SystemSteps,
            ],
            services.GetRequiredService<HostedModules>(),
            services.GetRequiredService<IDocumentStore>()));
        builder.Services.AddSingleton<SessionTokens>();
        builder.Services.AddSingleton<ModuleTokens>();
        // Reads each hosted module's settings schema from its describe answer whenever it is asked.
        builder.Services.AddSingleton(services => services.GetRequiredService<HostedModules>().SettingsSchemas);
        builder.Services.AddSingleton<ModuleSettingsService>();
        builder.Services.AddSingleton<FlowRunAdvancer>();
        builder.Services.AddSingleton<IFlowRunAdvancer>(services => services.GetRequiredService<FlowRunAdvancer>());
        builder.Services.AddHostedService(services => services.GetRequiredService<FlowRunAdvancer>());
        builder.Services.AddSingleton<FlowRunService>();
        builder.Services.AddSingleton<TriggerService>();
        builder.Services.AddSingleton<TriggerLoop>();

        // Registered after the runner, so it stops first: a start it makes while stopping is advanced on the next start.
        builder.Services.AddHostedService(services => services.GetRequiredService<TriggerLoop>());

        // Registered last, so it stops first: a delivery a stop interrupts is not counted as handled and comes again.
        builder.Services.AddSingleton<ModuleFeed>();
        builder.Services.AddHostedService(services => services.GetRequiredService<ModuleFeed>());

        builder.Services.AddOpenApi();

        WebApplication app = builder.Build();

        // The token check runs first for everything but /health; the DomainException mapping sits
        // inside it, wrapping the endpoints.
        app.UseDaemonToken(token, app.Services.GetRequiredService<SessionTokens>(), app.Services.GetRequiredService<ModuleTokens>());
        app.UseDomainExceptionMapping();

        app.MapDaemonEndpoints();
        app.MapTypeEndpoints();
        app.MapFlowEndpoints();
        app.MapAgentEndpoints();
        app.MapFlowRunEndpoints();
        app.MapProjectEndpoints();
        app.MapProjectModuleEndpoints();
        app.MapParcelEndpoints();

        // Served in every environment: the slim builder's environment is Production unless set.
        app.MapOpenApi();

        await app.StartAsync(cancellationToken);
        int boundPort = BoundPort(app);

        try
        {
            // The software module calls the core's API at the address the daemon now listens on,
            // presenting a token issued to it for this start.
            softwareCore.Connect(
                new Uri($"http://127.0.0.1:{boundPort}"),
                await app.Services.GetRequiredService<ModuleTokens>().ReissueAsync(SoftwareSteps.Module, cancellationToken));

            // A project registered before modules were has the default modules switched on, before any
            // of its live flow runs reaches a module's step and before a module's start reads it.
            await app.Services.GetRequiredService<ProjectService>().SwitchOnDefaultsAsync(cancellationToken);

            // A project an earlier build registered reads its remote as its first repo, and what that
            // build kept in the project's record of the software module's (the primary branch, the build
            // and test commands, the required checks) becomes the module's settings, before any step reads them.
            await app.Services.GetRequiredService<EarlierProjectRecords>().CarryAsync(cancellationToken);

            // Each clone an earlier build kept under clones/ moves to the per-repo layout with its
            // worktrees, and every repo of every project gets its clone, once each project has its repos
            // and before any module starts or any live flow run is advanced, so no module's git command
            // runs against a clone while the core moves or makes it.
            await app.Services.GetRequiredService<RepoClones>().StartAsync(cancellationToken);

            // Each hosted module is waited on for health and asked to describe itself once the API it
            // calls is listening, and before anything is advertised: flows are validated against its
            // answer and module settings are read through it. A module answers health only once its own
            // start is done; the software module's start sweeps the worktrees of flow runs that ended or
            // were stopped while no daemon ran, so that is done before any live flow run is advanced.
            await app.Services.GetRequiredService<HostedModules>().StartAsync(cancellationToken);

            // Types come from the store, so the modules' ones are in it before anything is served
            // or advertised: the address file is written only after this returns. Until install exists,
            // the start stands in for it, reading each module's definitions folder from its directory.
            await ImportModulesAsync(app.Services.GetRequiredService<DefinitionService>(), options, cancellationToken);

            // A flow version recorded before its module's argument schema refused its arguments still
            // reads, resolves, and runs; the start says which ones its module now refuses.
            await StoredFlowArguments.WarnAsync(
                app.Services.GetRequiredService<IDocumentStore>(),
                app.Services.GetRequiredService<FlowSystemSteps>(),
                app.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(StoredFlowArguments)),
                cancellationToken);

            // Each module's feed position is fixed before any live flow run is advanced, so on a node
            // where a feed has never run, an end a resumed flow run records lands past the position and
            // is delivered; everything at or below it ended before this start, and the module's own
            // start, done above, has already swept what those flow runs left. Fixing it before the
            // address is advertised also keeps anything recorded through the API from landing below it.
            ModuleFeed feed = app.Services.GetRequiredService<ModuleFeed>();
            await feed.FixPositionsAsync(cancellationToken);

            // Every live flow run that is not paused carries on from its current step.
            await app.Services.GetRequiredService<FlowRunAdvancer>().AdvanceLiveAsync(cancellationToken);

            // The trigger loop's position is fixed before the address is advertised, so nothing
            // recorded through the API can land before the point it reads from.
            TriggerLoop triggers = app.Services.GetRequiredService<TriggerLoop>();
            await triggers.FixPositionAsync(cancellationToken);
            triggers.Begin();
            feed.Begin();
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        var info = new DaemonInfo(Environment.ProcessId, boundPort, token, startedAt, Version);
        info.Save(home);
        app.Lifetime.ApplicationStopped.Register(() =>
        {
            DaemonInfo.Delete(home);
            homeLock.Dispose();
        });

        return new RunningDaemon(app, info, homeLock);
    }

    /// <summary>
    /// Imports the definitions of the software module, whose manifest must name it by the name its steps
    /// are prefixed with and projects switch it on by, then those of each module in
    /// <see cref="DaemonOptions.ModulePaths"/>, each module in one save.
    /// </summary>
    private static async Task ImportModulesAsync(DefinitionService definitions, DaemonOptions options, CancellationToken cancellationToken)
    {
        string softwarePath = options.SoftwareModulePath ?? DaemonOptions.DefaultSoftwareModulePath;
        ModuleArtifact software = await ModuleArtifact.ReadAsync(softwarePath, cancellationToken);
        if (!string.Equals(software.Module, SoftwareSteps.Module, StringComparison.Ordinal))
        {
            throw new DomainException(
                $"The software module's manifest in '{softwarePath}' names the module '{software.Module}'; it is '{SoftwareSteps.Module}', the name its steps are prefixed with and projects switch it on by.");
        }

        await definitions.ImportAsync(software, cancellationToken);
        foreach (string path in options.ModulePaths)
        {
            await definitions.ImportAsync(await ModuleArtifact.ReadAsync(path, cancellationToken), cancellationToken);
        }
    }

    private static int BoundPort(WebApplication app)
    {
        IServerAddressesFeature? addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        string address = addresses?.Addresses.FirstOrDefault() ?? throw new InvalidOperationException("The daemon bound no address.");
        return new Uri(address).Port;
    }
}

public sealed class RunningDaemon(WebApplication app, DaemonInfo info, HomeLock homeLock) : IAsyncDisposable
{
    public DaemonInfo Info { get; } = info;

    /// <summary>The daemon's services, so a test can write through a service no endpoint exposes yet.</summary>
    internal IServiceProvider Services => app.Services;

    public Task WaitForShutdownAsync(CancellationToken cancellationToken) => app.WaitForShutdownAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken) => await app.StopAsync(cancellationToken);

    /// <summary>Disposes the daemon, then releases the home's lock it held from the start of its start.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await app.DisposeAsync();
        }
        finally
        {
            homeLock.Dispose();
        }
    }
}
