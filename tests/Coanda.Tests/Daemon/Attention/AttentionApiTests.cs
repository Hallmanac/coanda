using System.Net;
using System.Text.Json;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Registry;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Modules;
using Coanda.Modules.Software;
using Coanda.Tests.Daemon.Modules;
using Fisher;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Attention;

/// <summary>
/// The attention query through the loopback API, as a person and as a module. The flow runs are written
/// before the daemon starts and left paused, ended, or stopped, so the daemon advances none of them.
/// </summary>
public sealed class AttentionApiTests
{
    private const string Alpha = "alpha";
    private const string Beta = "beta";
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task<AttentionListTests.Seeded> SeedAsync(NodeHome home)
    {
        await using IDocumentStore store = await SeededStore.OpenAsync(home.DatabasePath, SoftwareDefinitions.All);
        var clock = new AttentionListTests.Clock();
        AttentionListTests.Seeded seeded = await AttentionListTests.SeedAsync(store, clock, withRunning: false);
        await ProjectServices.Software(store, clock).SetModulesAsync(Principal.LocalOwner, Beta, [], None);
        return seeded;
    }

    [Fact]
    public async Task A_person_lists_every_projects_paused_flow_runs_or_one_projects_and_an_unregistered_project_is_refused()
    {
        using var temp = new TemporaryHome();
        AttentionListTests.Seeded seeded = await SeedAsync(temp.Home);
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);

        List<AttentionView> all = await client.GetAsync<List<AttentionView>>("/attention", None);
        Assert.Equal([seeded.Early, seeded.Middle, seeded.Late], all.Select(v => v.FlowRunId));
        AttentionView early = all[0];
        Assert.Equal(Alpha, early.Project);
        Assert.Equal(seeded.EarlyParcel, early.ParcelId);
        Assert.Equal("Early", early.ParcelTitle);
        Assert.Equal("build", early.FlowName);
        Assert.Equal("build", early.Step);
        Assert.Equal("Waiting on the API owner", early.Reason);
        Assert.Equal("Resume it once they answer", early.Lever);
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 9, 10, 0, TimeSpan.Zero), early.PausedAt);

        List<AttentionView> alpha = await client.GetAsync<List<AttentionView>>($"/attention?project={Alpha}", None);
        Assert.Equal([seeded.Early, seeded.Late], alpha.Select(v => v.FlowRunId));

        DomainException unregistered = await Assert.ThrowsAsync<DomainException>(() => client.GetAsync<List<AttentionView>>("/attention?project=gamma", None));
        Assert.Equal("Project 'gamma' is not registered; register it with coa project add gamma [--repo <url>].", unregistered.Message);

        // A project named with no value is refused rather than read as none named.
        DomainException blank = await Assert.ThrowsAsync<DomainException>(() => client.GetAsync<List<AttentionView>>("/attention?project=", None));
        Assert.Equal("A project name is required; none was given.", blank.Message);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_modules_token_is_answered_for_the_projects_its_module_is_switched_on_for_and_refused_elsewhere()
    {
        using var temp = new TemporaryHome();
        AttentionListTests.Seeded seeded = await SeedAsync(temp.Home);
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        string token = await daemon.Services.GetRequiredService<ModuleTokens>().MintAsync(SoftwareSteps.Module, None);
        using var http = new HttpClient { BaseAddress = new Uri(daemon.Info.BaseAddress) };

        (HttpStatusCode status, string body) = await SendAsync(http, token, "/attention");
        Assert.Equal(HttpStatusCode.OK, status);
        List<AttentionView>? views = JsonSerializer.Deserialize<List<AttentionView>>(body, JsonSerializerOptions.Web);
        Assert.NotNull(views);
        Assert.Equal([seeded.Early, seeded.Late], views.Select(v => v.FlowRunId));

        (HttpStatusCode refusedStatus, string refused) = await SendAsync(http, token, $"/attention?project={Beta}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refusedStatus);
        Assert.Equal(ModuleNode.NotSwitchedOn(SoftwareSteps.Module, Beta), JsonSerializer.Deserialize<ErrorView>(refused, JsonSerializerOptions.Web)?.Error);

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
