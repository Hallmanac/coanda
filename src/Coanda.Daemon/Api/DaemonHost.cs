using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Coanda.Core;
using Coanda.Core.Parcels;
using Coanda.Core.Definitions;
using Coanda.Core.Registry;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Parcels;
using Coanda.Core.Types;
using Coanda.Daemon.Store;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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

    public static async Task<RunningDaemon> StartAsync(NodeHome home, int port, CancellationToken cancellationToken)
    {
        home.EnsureExists();
        string token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(o => o.TimestampFormat = "yyyy-MM-dd HH:mm:ss ");
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddSingleton(home);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddCoandaStore(home);
        builder.Services.AddSingleton<DefinitionService>();
        builder.Services.AddSingleton<ParcelService>();

        WebApplication app = builder.Build();
        MapEndpoints(app, token);

        await app.StartAsync(cancellationToken);

        try
        {
            // Types come from the store, so the shipped ones are in it before anything is served
            // or advertised: the address file is written only after this returns.
            await app.Services.GetRequiredService<DefinitionService>()
                .SeedAsync(ShippedDefinition.ShippedParcelTypes, cancellationToken);
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

    private static void MapEndpoints(WebApplication app, string token)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path == "/health")
            {
                await next(context);
                return;
            }

            if (!context.Request.Headers.TryGetValue(TokenHeader, out var presented) || presented != token)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await context.Response.WriteAsJsonAsync(new ErrorView("The call carried no valid daemon token."));
                return;
            }

            await next(context);
        });

        app.MapGet("/health", () => Results.Ok(new { ok = true }));

        app.MapGet("/status", (NodeHome home, IHostApplicationLifetime _) =>
        {
            DaemonInfo? info = DaemonInfo.Load(home);
            return info is null
                ? Results.NotFound(new ErrorView("The daemon has not written its address yet."))
                : Results.Ok(new DaemonStatusView(info.Pid, info.Port, info.StartedAt, info.Version, home.Path, home.DatabasePath));
        });

        app.MapPost("/stop", (HttpContext context, IHostApplicationLifetime lifetime) =>
        {
            CommandRegistry.Require("daemon.stop", PrincipalOf(context));
            lifetime.StopApplication();
            return Results.Ok(new { stopping = true });
        });

        app.MapGet("/types", async (HttpContext context, ParcelService parcels, string project, CancellationToken cancellationToken) =>
        {
            IReadOnlyList<ParcelType> types = await parcels.TypesAsync(PrincipalOf(context), project, cancellationToken);
            return Results.Ok(types.Select(t => new TypeView(t.Name, t.Hash, t.States, t.Resolutions)).ToList());
        });

        app.MapGet("/parcels", async (HttpContext context, ParcelService parcels, string project, CancellationToken cancellationToken) =>
        {
            IReadOnlyList<Parcel> list = await parcels.ListAsync(PrincipalOf(context), project, cancellationToken);
            return Results.Ok(list.Select(ParcelView.From).ToList());
        });

        app.MapGet("/parcels/{id:guid}", async (HttpContext context, ParcelService parcels, Guid id, string project, CancellationToken cancellationToken) =>
        {
            Parcel? parcel = await parcels.ShowAsync(PrincipalOf(context), project, id, cancellationToken);
            return parcel is null
                ? Results.NotFound(new ErrorView($"No parcel {Ids.Short(id)} in project '{project}'."))
                : Results.Ok(ParcelView.From(parcel));
        });

        app.MapPost("/parcels", async (HttpContext context, ParcelService parcels, ParcelAddRequest request, CancellationToken cancellationToken) =>
        {
            Parcel parcel = await parcels.AddAsync(PrincipalOf(context), request.Project, request.Type, request.Title, request.State, cancellationToken);
            return Results.Created($"/parcels/{parcel.Id}", ParcelView.From(parcel));
        });

        app.MapPost("/parcels/move", async (HttpContext context, ParcelService parcels, ParcelMoveRequest request, CancellationToken cancellationToken) =>
        {
            Parcel parcel = await parcels.MoveAsync(PrincipalOf(context), request.Project, request.ParcelId, request.To, cancellationToken);
            return Results.Ok(ParcelView.From(parcel));
        });

        app.MapPost("/parcels/close", async (HttpContext context, ParcelService parcels, ParcelCloseRequest request, CancellationToken cancellationToken) =>
        {
            Parcel parcel = await parcels.CloseAsync(PrincipalOf(context), request.Project, request.ParcelId, request.Resolution, request.Reason, cancellationToken);
            return Results.Ok(ParcelView.From(parcel));
        });

        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (DomainException exception)
            {
                context.Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
                await context.Response.WriteAsJsonAsync(new ErrorView(exception.Message));
            }
        });
    }

    /// <summary>Phase 0 knows one principal: whoever holds the token is the local owner.</summary>
    private static Principal PrincipalOf(HttpContext context) =>
        context.Request.Headers.TryGetValue(PrincipalHeader, out var id) && !string.IsNullOrWhiteSpace(id)
            ? new Principal(id.ToString(), Role.Owner)
            : Principal.LocalOwner;
}

public sealed class RunningDaemon(WebApplication app, DaemonInfo info) : IAsyncDisposable
{
    public DaemonInfo Info { get; } = info;

    public Task WaitForShutdownAsync(CancellationToken cancellationToken) => app.WaitForShutdownAsync(cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken) => await app.StopAsync(cancellationToken);

    public async ValueTask DisposeAsync() => await app.DisposeAsync();
}
