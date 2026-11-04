using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Core.Types;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Definitions;
using Coanda.Tests.Core;
using Fisher;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon;

public sealed class ParcelBindingTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    private static async Task PublishTypeAsync(RunningDaemon daemon, ParcelType type)
    {
        DefinitionService definitions = daemon.Services.GetRequiredService<DefinitionService>();
        Definition draft = await definitions.DraftParcelTypeAsync(Principal.LocalOwner, DefinitionScope.Project, Project, type, null, None);
        await definitions.PublishAsync(Principal.LocalOwner, Project, draft.Id, None);
    }

    [Fact]
    public async Task On_a_fresh_node_home_the_daemon_resolves_both_shipped_types_at_the_versions_carrying_their_bindings()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, None);
        var resolver = new DefinitionResolver(daemon.Services.GetRequiredService<IDocumentStore>());

        ParcelType? task = await resolver.ResolveParcelTypeAsync(Project, "task", None);
        ParcelType? idea = await resolver.ResolveParcelTypeAsync(Project, "idea", None);

        task.Should().NotBeNull();
        task.Hash.Should().Be(ShippedTypes.Task.Hash);
        task.ContourBindings.Should().Equal(new ContourBinding("build", "build"));
        idea.Should().NotBeNull();
        idea.Hash.Should().Be(ShippedTypes.Idea.Hash);
        idea.ContourBindings.Should().Equal(new ContourBinding("discovery", "discovery"));

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_gate_given_to_the_host_and_bound_by_a_project_type_refuses_the_move_through_the_move_endpoint_with_its_reason()
    {
        using var temp = new TemporaryHome();
        var gate = new StandInGate();
        await using RunningDaemon daemon = await DaemonHost.StartAsync(temp.Home, 0, new DaemonOptions { Gates = [gate] }, None);
        using var client = new DaemonClient(temp.Home);
        ParcelType gated = ShippedTypes.Task with
        {
            Name = "gated-task",
            ContourBindings = [],
            GateBindings = [new GateBinding("refine", "build", gate.Name)],
        };
        await PublishTypeAsync(daemon, gated);

        ParcelView unready = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "gated-task", "unready work", null), None);
        await FluentActions.Awaiting(() => client.PostAsync<ParcelView>("/parcels/move", new ParcelMoveRequest(Project, unready.Id, "build"), None))
            .Should().ThrowAsync<DomainException>()
            .WithMessage($"The gate 'stand-in' refused the move from 'refine' to 'build': {StandInGate.Reason}");
        (await client.GetAsync<ParcelView>($"/parcels/{unready.Id}?project={Project}", None)).State.Should().Be("refine");

        ParcelView ready = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "gated-task", "ready work", null), None);
        (await client.PostAsync<ParcelView>("/parcels/move", new ParcelMoveRequest(Project, ready.Id, "build"), None)).State.Should().Be("build");

        ParcelType unknown = gated with { Name = "unknown-gate", GateBindings = [new GateBinding("refine", "build", "not-given")] };
        await FluentActions.Awaiting(() => PublishTypeAsync(daemon, unknown))
            .Should().ThrowAsync<DomainException>().WithMessage("*binds the gate 'not-given', which is not a gate this daemon runs*");

        await daemon.StopAsync(None);
    }
}
