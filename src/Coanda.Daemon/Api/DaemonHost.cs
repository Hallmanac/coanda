using System.Reflection;
using System.Security.Cryptography;
using Coanda.Core.Definitions;
using Coanda.Core.Parcels;
using Coanda.Daemon.Actions;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Api.Middleware;
using Coanda.Daemon.Contours;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Flows;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Store;
using Coanda.Daemon.Triggers;
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
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.JudgmentLimit, TimeSpan.Zero, nameof(options));
        home.EnsureExists();

        // Before the store is configured, which would add this build's tables to an old record.
        PreRenameStore.ThrowIfFound(home);
        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ");
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddSingleton(home);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddCoandaStore(home);
        builder.Services.AddSingleton(new GateSet(options.Gates));
        builder.Services.AddSingleton<DefinitionService>();
        builder.Services.AddSingleton<ParcelService>();
        builder.Services.AddSingleton<ContourService>();
        builder.Services.AddSingleton<IAgentExecutor>(options.Executor ?? new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>()));
        builder.Services.AddSingleton(new FlowRunnerOptions(options.JudgmentLimit));
        builder.Services.AddSingleton(services => FlowActions.Shipped(
            services.GetRequiredService<ParcelService>(), services.GetRequiredService<TimeProvider>()));
        builder.Services.AddSingleton<FlowRunner>();
        builder.Services.AddSingleton<IFlowAdvancer>(services => services.GetRequiredService<FlowRunner>());
        builder.Services.AddHostedService(services => services.GetRequiredService<FlowRunner>());
        builder.Services.AddSingleton<FlowService>();
        builder.Services.AddSingleton<TriggerService>();
        builder.Services.AddSingleton<TriggerLoop>();

        // Registered after the runner, so it stops first: a start it makes while stopping is advanced on the next start.
        builder.Services.AddHostedService(services => services.GetRequiredService<TriggerLoop>());

        WebApplication app = builder.Build();

        // The token check runs first for everything but /health; the DomainException mapping sits
        // inside it, wrapping the endpoints.
        app.UseDaemonToken(token);
        app.UseDomainExceptionMapping();

        app.MapDaemonEndpoints();
        app.MapTypeEndpoints();
        app.MapContourEndpoints();
        app.MapAgentEndpoints();
        app.MapFlowEndpoints();
        app.MapParcelEndpoints();

        await app.StartAsync(cancellationToken);

        try
        {
            // Types come from the store, so the shipped ones are in it before anything is served
            // or advertised: the address file is written only after this returns.
            await app.Services.GetRequiredService<DefinitionService>()
                .SeedAsync(ShippedDefinition.All, cancellationToken);

            // Every live flow that is not paused carries on from its current step.
            await app.Services.GetRequiredService<FlowRunner>().AdvanceLiveAsync(cancellationToken);

            // The trigger loop's position is fixed before the address is advertised, so nothing
            // recorded through the API can land before the point it reads from.
            TriggerLoop triggers = app.Services.GetRequiredService<TriggerLoop>();
            await triggers.FixPositionAsync(cancellationToken);
            triggers.Begin();
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }

        int boundPort = BoundPort(app);
        var info = new DaemonInfo(Environment.ProcessId, boundPort, token, startedAt, Version);
        info.Save(home);
        app.Lifetime.ApplicationStopped.Register(() => DaemonInfo.Delete(home));

        return new RunningDaemon(app, info);
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
