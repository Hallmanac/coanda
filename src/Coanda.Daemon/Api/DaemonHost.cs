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
        // Narrows what an earlier run left wider and creates the database owner-only, before the
        // check below or the store opens it.
        home.EnsureExists().EnsureDatabase();
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
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddCoandaStore(home);
        // The shipped gates run whatever the options name; the options add gates, never remove one.
        builder.Services.AddSingleton(new GateSet([.. GateSet.ShippedGates, .. options.Gates]));
        // Flows are validated against the core's system steps and the steps the modules switched on for
        // the flow's project describe, or every hosted module's for a definition of no project.
        builder.Services.AddSingleton(services => new DefinitionService(
            services.GetRequiredService<IDocumentStore>(),
            services.GetRequiredService<TimeProvider>(),
            services.GetRequiredService<GateSet>(),
            services.GetRequiredService<FlowSystemSteps>().NamesAsync));
        builder.Services.AddSingleton(new CredentialProfiles(options.Profiles));
        builder.Services.AddSingleton<ProjectService>();
        builder.Services.AddSingleton<ParcelService>();
        builder.Services.AddSingleton<FlowService>();
        builder.Services.AddSingleton<IAgentExecutor>(options.Executor ?? new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>()));
        builder.Services.AddSingleton(new FlowRunAdvancerOptions(options.AgentStepLimit));
        builder.Services.AddSingleton<ICommandRunner>(options.CommandRunner ?? new NoCommandRunner());
        builder.Services.AddSingleton<FlowRunWorkers>();

        // The software module, hosted in this process behind its host adapter. It is given the node
        // home's locations, the command runner, the GitHub handler, the gates command limit, and the
        // core's address and its token, which exist only once the daemon is listening, and it reaches
        // the daemon otherwise only through the core's API.
        var softwareCore = new ModuleCoreAccess(SoftwareSteps.Module);
        builder.Services.AddSingleton(softwareCore);
        builder.Services.AddSingleton(services => new SoftwareModule(
            new SoftwareModuleOptions
            {
                HomePath = home.Path,
                ClonesPath = home.ClonesPath,
                WorkPath = home.WorkPath,
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

            // Every live flow run that is not paused carries on from its current step.
            await app.Services.GetRequiredService<FlowRunAdvancer>().AdvanceLiveAsync(cancellationToken);

            // The trigger loop's position is fixed before the address is advertised, so nothing
            // recorded through the API can land before the point it reads from.
            TriggerLoop triggers = app.Services.GetRequiredService<TriggerLoop>();
            await triggers.FixPositionAsync(cancellationToken);
            triggers.Begin();

            // Each module's feed position is fixed before the address is advertised too, for the same reason.
            ModuleFeed feed = app.Services.GetRequiredService<ModuleFeed>();
            await feed.FixPositionsAsync(cancellationToken);
            feed.Begin();
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        var info = new DaemonInfo(Environment.ProcessId, boundPort, token, startedAt, Version);
        info.Save(home);
        app.Lifetime.ApplicationStopped.Register(() => DaemonInfo.Delete(home));

        return new RunningDaemon(app, info);
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

public sealed class RunningDaemon(WebApplication app, DaemonInfo info) : IAsyncDisposable
{
    public DaemonInfo Info { get; } = info;

    /// <summary>The daemon's services, so a test can write through a service no endpoint exposes yet.</summary>
    internal IServiceProvider Services => app.Services;

    public Task WaitForShutdownAsync(CancellationToken cancellationToken) => app.WaitForShutdownAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken) => await app.StopAsync(cancellationToken);

    public async ValueTask DisposeAsync() => await app.DisposeAsync();
}
