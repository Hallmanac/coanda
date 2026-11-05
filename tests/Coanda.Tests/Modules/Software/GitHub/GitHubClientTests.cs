using System.Text.Json;
using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Daemon.Api;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Coanda.Modules.Software;
using Coanda.Modules.Software.GitHub;
using Coanda.Sdk.Contract;
using Coanda.Tests.Daemon;
using Coanda.Tests.Daemon.Commands;
using Coanda.Tests.Daemon.FlowRuns;
using Fisher;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Coanda.Tests.Modules.Software.GitHub;

public sealed class GitHubClientTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task The_registered_clients_trace_log_shows_the_Authorization_header_redacted_and_never_the_token()
    {
        const string Token = "ghp_neverInTheLog0123456789";
        var github = new FakeGitHub();
        var log = new RecordingLogger();
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(log));
        GitHubClient.Register(services, github.Handler);
        await using ServiceProvider provider = services.BuildServiceProvider();

        GitHubPullRequest? found = await provider.GetRequiredService<GitHubClient>()
            .FindOpenAsync(GitHubRepository.Parse("https://github.com/example/repo") ?? throw new InvalidOperationException("The remote parses."), "coanda/abc", Token, None);

        Assert.Null(found);
        FakeGitHub.Sent sent = Assert.Single(github.Requests);
        Assert.Equal($"Bearer {Token}", sent.Authorization);
        Assert.Equal("https://api.github.com", sent.Uri.GetLeftPart(UriPartial.Authority));
        // the trace log lists the request's headers
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Trace && e.Message.Contains("Authorization", StringComparison.Ordinal));
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains(Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_host_whose_options_supply_no_GitHub_handler_fails_every_request_without_sending_it()
    {
        using var temp = new TemporaryHome();

        // Started on its own rather than through Daemons, which would supply a fake GitHub.
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { CommandRunner = new ScriptedCommandRunner() }, None);
        await RegisteredProjects.AddAsync(daemon, SoftwareNode.Project, None);
        await Publishing.FlowAsync(daemon, SoftwareNode.Project, SoftwareNode.Steps);
        Parcel parcel = await daemon.Services.GetRequiredService<ParcelService>().AddAsync(Principal.LocalOwner, SoftwareNode.Project, "task", "Nowhere to send it", null, None);
        var flowRuns = new FlowRunService(daemon.Services.GetRequiredService<IDocumentStore>(), TimeProvider.System);
        FlowRun started = await flowRuns.StartAsync(Principal.LocalOwner, SoftwareNode.Project, parcel.Id, SoftwareNode.Steps.Name, None);
        await flowRuns.StartStepAsync(Principal.LocalOwner, SoftwareNode.Project, started.Id, "open-pr", None);
        FlowRun held = await flowRuns.DispatchedAsync(Principal.LocalOwner, SoftwareNode.Project, started.Id, "open-pr", None);

        DispatchAnswer answer = await daemon.Services.GetRequiredService<SoftwareModule>().DispatchAsync(
            new DispatchRequest(
                SoftwareNode.Project, parcel.Id, started.Id, SoftwareSteps.OpenPr, 1, held.Current?.DispatchId ?? Guid.Empty, false,
                temp.Home.FlowRunWorkPath(SoftwareNode.Project, started.Id), new Dictionary<string, JsonElement>()),
            None);

        DispatchAnswer.Paused paused = Assert.IsType<DispatchAnswer.Paused>(answer);
        Assert.Contains("could not reach GitHub", paused.Reason, StringComparison.Ordinal);
        Assert.Contains("no GitHub handler", paused.Reason, StringComparison.Ordinal);

        await daemon.StopAsync(None);
    }
}
