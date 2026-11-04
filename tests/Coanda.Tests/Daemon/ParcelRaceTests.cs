using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Core.Types;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Definitions;
using Fisher;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon;

/// <summary>
/// A write to an existing parcel whose stream another write reaches between the service's read and
/// its save is refused, and records nothing.
/// </summary>
public sealed class ParcelRaceTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_move_whose_parcel_changes_between_its_read_and_its_save_is_refused_and_records_nothing()
    {
        using var temp = new TemporaryHome();
        var racing = new RacingGate();
        await using RunningDaemon daemon = await StartAsync(temp, racing);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "raced-task", "Raced move", null), None);

        racing.Arm(daemon.Services.GetRequiredService<IDocumentStore>(), parcel.Id);
        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => client.PostAsync<ParcelView>("/parcels/move", new ParcelMoveRequest(Project, parcel.Id, "build"), None));
        Assert.Equal(Refusal(parcel.Id), exception.Message);

        Assert.True(racing.Raced);
        Assert.Equal("refine", (await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", None)).Stage);
        Assert.DoesNotContain(await EventsAsync(daemon, parcel.Id), e => e is ParcelMoved);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_close_whose_parcel_changes_between_its_read_and_its_save_is_refused_and_records_nothing()
    {
        using var temp = new TemporaryHome();
        var racing = new RacingGate();
        await using RunningDaemon daemon = await StartAsync(temp, racing);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "raced-task", "Raced close", null), None);

        racing.Arm(daemon.Services.GetRequiredService<IDocumentStore>(), parcel.Id);
        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => client.PostAsync<ParcelView>(
                "/parcels/close", new ParcelCloseRequest(Project, parcel.Id, "abandoned", "Not needed."), None));
        Assert.Equal(Refusal(parcel.Id), exception.Message);

        Assert.True(racing.Raced);
        Assert.Equal("refine", (await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", None)).Stage);
        Assert.DoesNotContain(await EventsAsync(daemon, parcel.Id), e => e is ParcelClosed);

        await daemon.StopAsync(None);
    }

    private static string Refusal(Guid parcelId) =>
        $"Parcel {Ids.Short(parcelId)} changed while this change was being recorded, so nothing was. Read it again and retry.";

    private static async Task<RunningDaemon> StartAsync(TemporaryHome temp, RacingGate gate)
    {
        RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Gates = [gate] }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        ParcelType raced = ShippedTypes.Task with
        {
            Name = "raced-task",
            FlowBindings = [],
            Fields = [],
            GateBindings =
            [
                new GateBinding("refine", "build", gate.Name),
                new GateBinding("refine", ShippedTypes.Task.TerminalStage, gate.Name),
            ],
        };
        DefinitionService definitions = daemon.Services.GetRequiredService<DefinitionService>();
        Definition draft = await definitions.DraftParcelTypeAsync(Principal.LocalOwner, DefinitionScope.Project, Project, raced, null, None);
        await definitions.PublishAsync(Principal.LocalOwner, Project, draft.Id, None);
        return daemon;
    }

    private static async Task<IReadOnlyList<object>> EventsAsync(RunningDaemon daemon, Guid parcelId)
    {
        await using IQuerySession session = daemon.Services.GetRequiredService<IDocumentStore>().QuerySession(Project);
        IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(parcelId, token: None);
        return [.. events.Select(e => e.Data)];
    }

    /// <summary>
    /// A gate that, once armed, passes its next check after appending a flow run's attach to the parcel's
    /// stream from a session of its own, as a flow run starting on the parcel at that moment would.
    /// </summary>
    private sealed class RacingGate : IGate
    {
        private IDocumentStore? store;
        private Guid parcelId;

        public string Name => "racing";

        public bool Raced { get; private set; }

        public void Arm(IDocumentStore racingStore, Guid racedParcel)
        {
            store = racingStore;
            parcelId = racedParcel;
        }

        public GateVerdict Check(Parcel parcel, ParcelType type, string fromStage, string toStage)
        {
            if (store is { } armed && parcel.Id == parcelId)
            {
                store = null;
                using IDocumentSession session = armed.LightweightSession(Project);
                session.Events.Append(parcelId, new ParcelFlowRunAttached(parcelId, Ids.New(), DateTimeOffset.UtcNow));
                session.SaveChangesAsync(None).GetAwaiter().GetResult();
                Raced = true;
            }

            return GateVerdict.Pass;
        }
    }
}
