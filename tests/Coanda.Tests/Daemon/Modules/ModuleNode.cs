using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Coanda.Core.FlowRuns;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Projects;
using Coanda.Tests.Daemon.FlowRuns;
using Coanda.Tests.Daemon.SystemSteps;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Modules;

/// <summary>
/// A running daemon set up for a module: the projects <see cref="On"/>, which the test module
/// <see cref="Module"/> is switched on for, and <see cref="Off"/>, which it is not, each with the flow
/// naming <see cref="RunningSystemStep"/> published, whose step name carries the test module's prefix.
/// </summary>
internal sealed class ModuleNode : IAsyncDisposable
{
    public const string On = "alpha";
    public const string Off = "beta";
    public const string Module = "test";
    public const string Other = "other";

    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);
    private static readonly Flow Dispatching = RunningSystemStep.FlowNamed();

    private readonly TemporaryHome temp;

    private ModuleNode(TemporaryHome temp, RunningDaemon daemon, RunningSystemStep step)
    {
        this.temp = temp;
        Daemon = daemon;
        Step = step;
    }

    public RunningDaemon Daemon { get; }

    public RunningSystemStep Step { get; }

    public NodeHome Home => temp.Home;

    public IServiceProvider Services => Daemon.Services;

    /// <summary>Starts the node; <paramref name="settingsSchemas"/> are the stand-in modules' settings schemas, none when it is null.</summary>
    public static async Task<ModuleNode> StartAsync(IModuleSettingsSchemas? settingsSchemas = null)
    {
        var temp = new TemporaryHome();
        var step = new RunningSystemStep();
        RunningDaemon daemon = await Daemons.StartAsync(
            temp.Home, new DaemonOptions { SystemSteps = [step], ModuleSettingsSchemas = settingsSchemas }, None);
        var node = new ModuleNode(temp, daemon, step);
        foreach (string project in new[] { On, Off })
        {
            await RegisteredProjects.AddAsync(daemon, project, None);
            await Publishing.FlowAsync(daemon, project, Dispatching);
        }

        await node.SwitchOnAsync(On, Module);
        return node;
    }

    public Task<string> TokenAsync(string module) => Services.GetRequiredService<ModuleTokens>().IssueAsync(module, None);

    public Task SwitchOnAsync(string project, params string[] modules) =>
        Services.GetRequiredService<ProjectService>().SetModulesAsync(Principal.LocalOwner, project, modules, None);

    public async Task<Parcel> ParcelAsync(string project, string title = "For the module") =>
        await Services.GetRequiredService<ParcelService>().AddAsync(Principal.LocalOwner, project, "task", title, null, None);

    /// <summary>Starts the dispatching flow on a new parcel and waits for its step to answer running.</summary>
    public async Task<(Guid ParcelId, Guid FlowRunId, Guid DispatchId)> DispatchedAsync(string project, string title = "Dispatched")
    {
        Parcel parcel = await ParcelAsync(project, title);
        FlowRunService flowRuns = Services.GetRequiredService<FlowRunService>();
        FlowRun started = await flowRuns.StartAsync(Principal.LocalOwner, project, parcel.Id, Dispatching.Name, None);
        await Services.GetRequiredService<IFlowRunAdvancer>().RequestAdvance(project, started.Id).WaitAsync(Patience);
        await Services.GetRequiredService<FlowRunWorkers>().ReturnedAsync(started.Id, None).WaitAsync(Patience);
        FlowRun running = await flowRuns.ShowAsync(Principal.LocalOwner, project, started.Id, None)
            ?? throw new InvalidOperationException($"No flow run {started.Id}.");
        Assert.Equal(FlowRunStatus.Running, running.Status);
        return (parcel.Id, started.Id, running.Current?.DispatchId ?? throw new InvalidOperationException("The flow run holds no dispatch id."));
    }

    /// <summary>
    /// The status and the body of a call presenting <paramref name="token"/>, or none, carrying
    /// <paramref name="principalHeader"/> when it is given.
    /// </summary>
    public async Task<(HttpStatusCode Status, string Body)> SendAsync(
        string? token, HttpMethod method, string path, object? body = null, string? principalHeader = null)
    {
        using var http = new HttpClient { BaseAddress = new Uri(Daemon.Info.BaseAddress) };
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        if (token is not null)
        {
            request.Headers.Add(DaemonHost.TokenHeader, token);
        }

        if (principalHeader is not null)
        {
            request.Headers.Add(DaemonHost.PrincipalHeader, principalHeader);
        }

        using HttpResponseMessage response = await http.SendAsync(request, None);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(None));
    }

    /// <summary>Asserts the call is refused with a domain rule, and gives the rule.</summary>
    public async Task<string> RefusedAsync(string token, HttpMethod method, string path, object? body = null)
    {
        (HttpStatusCode status, string answer) = await SendAsync(token, method, path, body);
        Assert.True(status == HttpStatusCode.UnprocessableEntity, $"{method} {path} was answered {status}: {answer}");
        return JsonSerializer.Deserialize<ErrorView>(answer, JsonSerializerOptions.Web)?.Error ?? string.Empty;
    }

    public static string NotSwitchedOn(string module, string project) =>
        $"Module '{module}' is not switched on for project '{project}'; a module acts only in a project it is switched on for.";

    public static string NotOpen(string entry) => $"'{entry}' is not open to the Module role.";

    public async ValueTask DisposeAsync()
    {
        await Daemon.StopAsync(None);
        await Daemon.DisposeAsync();
        temp.Dispose();
    }
}
