using Coanda.Core;
using Coanda.Core.Registry;
using Coanda.Core.Triggers;
using Coanda.Core.Triggers.Events;
using Coanda.Daemon;
using Coanda.Daemon.Store;
using Coanda.Daemon.Triggers;
using Coanda.Tests.Daemon.Modules;
using Fisher;
using JasperFx.Events;
using JasperFx.Events.Documents;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Triggers;

/// <summary>
/// Recording an external event at the service: it carries the id the outside world gives it and the
/// parcel id its recorder names, and recording the same project, kind, and external id again records
/// nothing and gives back the event already recorded, after every rule has been checked.
/// </summary>
public sealed class ExternalEventRecordingTests
{
    private const string Alpha = "alpha";
    private const string Beta = "beta";
    private const string Kind = "pr-assigned";
    private static readonly CancellationToken None = CancellationToken.None;
    private static readonly Principal Teammate = new("teammate", Role.Member);
    private static readonly Dictionary<string, string> Fields = new() { ["assignee"] = "brian" };

    /// <summary>A clock that moves a second forward each time it is read, so two recordings never share a time.</summary>
    private sealed class SteppingClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 18, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => now = now.AddSeconds(1);
    }

    private static async Task<IReadOnlyList<ExternalEvent>> StoredAsync(IDocumentStore store, string project)
    {
        await using IQuerySession session = store.QuerySession(project);
        return await session.Query<ExternalEvent>().ToListAsync(None);
    }

    private static async Task<IReadOnlyList<ExternalEventRecorded>> AppendedAsync(IDocumentStore store, string project)
    {
        await using IQuerySession session = store.QuerySession(project);
        IReadOnlyList<IEvent> events = await session.Events.QueryEventsAsync(e => e.EventTypeName == "external_event_recorded", None);
        return [.. events.Select(e => e.Data).OfType<ExternalEventRecorded>()];
    }

    [Fact]
    public async Task An_external_event_reads_back_with_its_external_id_and_its_parcel_id_or_with_no_parcel_id_when_none_was_given()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        var triggers = new TriggerService(store, TimeProvider.System);
        Guid parcel = Ids.New();

        ExternalEvent named = await triggers.RecordEventAsync(Principal.LocalOwner, Alpha, Kind, "MDExOlB1bGxSZXF1ZXN0MTI=", parcel, Fields, None);
        ExternalEvent unnamed = await triggers.RecordEventAsync(Principal.LocalOwner, Alpha, Kind, "delivery-2", null, Fields, None);

        Assert.Equal(("MDExOlB1bGxSZXF1ZXN0MTI=", parcel), (named.ExternalId, named.ParcelId));
        Assert.Equal(("delivery-2", (Guid?)null), (unnamed.ExternalId, unnamed.ParcelId));

        IReadOnlyList<ExternalEvent> stored = await StoredAsync(store, Alpha);
        Assert.Equal(("MDExOlB1bGxSZXF1ZXN0MTI=", parcel), stored.Where(e => e.Id == named.Id).Select(e => (e.ExternalId, e.ParcelId)).Single());
        Assert.Equal(("delivery-2", (Guid?)null), stored.Where(e => e.Id == unnamed.Id).Select(e => (e.ExternalId, e.ParcelId)).Single());

        IReadOnlyList<ExternalEventRecorded> appended = await AppendedAsync(store, Alpha);
        Assert.Equal(("MDExOlB1bGxSZXF1ZXN0MTI=", parcel), appended.Where(e => e.EventId == named.Id).Select(e => (e.ExternalId, e.ParcelId)).Single());
        Assert.Equal(("delivery-2", (Guid?)null), appended.Where(e => e.EventId == unnamed.Id).Select(e => (e.ExternalId, e.ParcelId)).Single());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task An_external_event_with_a_missing_or_blank_external_id_is_refused_and_nothing_is_recorded(string? externalId)
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        var triggers = new TriggerService(store, TimeProvider.System);

        DomainException refused = await Assert.ThrowsAsync<DomainException>(
            () => triggers.RecordEventAsync(Principal.LocalOwner, Alpha, Kind, externalId, null, Fields, None));

        Assert.Equal("An external event needs the id the outside world gives it, which recording it again matches on; none was given.", refused.Message);
        Assert.Empty(await StoredAsync(store, Alpha));
        Assert.Empty(await AppendedAsync(store, Alpha));
    }

    [Fact]
    public async Task Recording_the_same_project_kind_and_external_id_again_appends_nothing_and_returns_the_event_already_recorded()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        var triggers = new TriggerService(store, new SteppingClock());
        Guid parcel = Ids.New();

        ExternalEvent first = await triggers.RecordEventAsync(Principal.LocalOwner, Alpha, Kind, "delivery-1", parcel, Fields, None);
        ExternalEvent again = await triggers.RecordEventAsync(
            Teammate, Alpha, Kind, "delivery-1", Ids.New(), new Dictionary<string, string> { ["assignee"] = "someone-else" }, None);
        ExternalEvent unnamed = await triggers.RecordEventAsync(Teammate, Alpha, Kind, "delivery-1", null, Fields, None);

        foreach (ExternalEvent returned in new[] { again, unnamed })
        {
            Assert.Equal(first.Id, returned.Id);
            Assert.Equal(parcel, returned.ParcelId);
            Assert.Equal(Principal.LocalOwner.Id, returned.RecordedBy);
            Assert.Equal(first.RecordedAt, returned.RecordedAt);
            Assert.Equivalent(Fields, returned.Fields, strict: true);
        }

        Assert.Equal([first.Id], (await AppendedAsync(store, Alpha)).Select(e => e.EventId));
        Assert.Equal([first.Id], (await StoredAsync(store, Alpha)).Select(e => e.Id));
    }

    [Fact]
    public async Task The_same_external_id_under_another_kind_in_another_project_or_in_another_letter_case_records_another_event()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        var triggers = new TriggerService(store, TimeProvider.System);

        ExternalEvent first = await triggers.RecordEventAsync(Principal.LocalOwner, Alpha, Kind, "Delivery-1", null, Fields, None);
        ExternalEvent otherKind = await triggers.RecordEventAsync(Principal.LocalOwner, Alpha, "pr-opened", "Delivery-1", null, Fields, None);
        ExternalEvent otherProject = await triggers.RecordEventAsync(Principal.LocalOwner, Beta, Kind, "Delivery-1", null, Fields, None);
        ExternalEvent otherCase = await triggers.RecordEventAsync(Principal.LocalOwner, Alpha, Kind, "delivery-1", null, Fields, None);
        ExternalEvent padded = await triggers.RecordEventAsync(Principal.LocalOwner, Alpha, Kind, " Delivery-1", null, Fields, None);

        Assert.Equal(5, new[] { first.Id, otherKind.Id, otherProject.Id, otherCase.Id, padded.Id }.Distinct().Count());
        Assert.Equal(" Delivery-1", padded.ExternalId);
        Assert.Equivalent(new[] { first.Id, otherKind.Id, otherCase.Id, padded.Id }, (await AppendedAsync(store, Alpha)).Select(e => e.EventId), strict: true);
        Assert.Equal([otherProject.Id], (await AppendedAsync(store, Beta)).Select(e => e.EventId));
    }

    [Fact]
    public async Task Two_recordings_of_the_same_event_at_the_same_time_leave_one_event_and_both_return_it()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        var triggers = new TriggerService(store, TimeProvider.System);
        using var go = new ManualResetEventSlim();

        Task<ExternalEvent> Recording(Principal by) => Task.Run(
            async () =>
            {
                go.Wait(None);
                return await triggers.RecordEventAsync(by, Alpha, Kind, "delivery-1", null, Fields, None);
            },
            None);

        Task<ExternalEvent> one = Recording(Principal.LocalOwner);
        Task<ExternalEvent> other = Recording(Teammate);
        go.Set();
        ExternalEvent[] both = await Task.WhenAll(one, other);

        ExternalEventRecorded appended = Assert.Single(await AppendedAsync(store, Alpha));
        Assert.Equal([appended.EventId, appended.EventId], both.Select(e => e.Id));
        Assert.Single(await StoredAsync(store, Alpha));
    }

    [Fact]
    public async Task An_event_recorded_before_external_ids_reads_with_neither_member_and_matches_no_later_recording()
    {
        using var temp = new TemporaryHome();
        Guid earlier;
        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            earlier = (await new TriggerService(store, TimeProvider.System).RecordEventAsync(Principal.LocalOwner, Alpha, Kind, "delivery-1", null, Fields, None)).Id;
        }

        // As the builds before external ids wrote it: the event and its snapshot without either member at all.
        SqliteConnection.ClearAllPools();
        using (SqliteConnection connection = RecordHomes.Open(temp.Home.DatabasePath))
        using (SqliteCommand stripped = connection.CreateCommand())
        {
            stripped.CommandText =
                "UPDATE fi_events SET data = json_remove(data, '$.externalId', '$.parcelId') WHERE type = 'external_event_recorded';"
                + "UPDATE fi_doc_externalevent SET data = json_remove(data, '$.externalId', '$.parcelId');";
            Assert.Equal(2, stripped.ExecuteNonQuery());
        }

        Assert.All(RecordHomes.Documents(temp.Home.DatabasePath, "fi_doc_externalevent"), d => Assert.DoesNotContain("externalId", d.Data, StringComparison.OrdinalIgnoreCase));

        await using IDocumentStore reopened = StoreConfiguration.Open(temp.Home.DatabasePath);
        ExternalEvent read = Assert.Single(await StoredAsync(reopened, Alpha));
        Assert.Equal((earlier, (string?)null, (Guid?)null), (read.Id, read.ExternalId, read.ParcelId));
        ExternalEventRecorded replayed = Assert.Single(await AppendedAsync(reopened, Alpha));
        Assert.Equal(((string?)null, (Guid?)null), (replayed.ExternalId, replayed.ParcelId));

        ExternalEvent later = await new TriggerService(reopened, TimeProvider.System).RecordEventAsync(Principal.LocalOwner, Alpha, Kind, "delivery-1", null, Fields, None);
        Assert.NotEqual(earlier, later.Id);
        Assert.Equal(2, (await AppendedAsync(reopened, Alpha)).Count);
    }

    [Fact]
    public async Task A_recording_the_rules_refuse_is_refused_though_the_same_event_is_recorded_and_carries_nothing_of_it()
    {
        await using ModuleNode node = await ModuleNode.StartAsync();
        TriggerService triggers = node.Services.GetRequiredService<TriggerService>();
        Principal module = Principal.ForModule(ModuleNode.Module);
        Guid parcel = Ids.New();
        var recordedFields = new Dictionary<string, string> { ["number"] = "4711-recorded" };
        ExternalEvent byModule = await triggers.RecordEventAsync(module, ModuleNode.On, "test/pr-opened", "node-id-1", parcel, recordedFields, None);
        ExternalEvent byPerson = await triggers.RecordEventAsync(Principal.LocalOwner, ModuleNode.On, Kind, "node-id-1", parcel, recordedFields, None);

        string[] refusals =
        [
            (await Assert.ThrowsAsync<DomainException>(() => triggers.RecordEventAsync(
                new Principal("build-session", Role.Agent), ModuleNode.On, Kind, "node-id-1", null, Fields, None))).Message,
            (await Assert.ThrowsAsync<DomainException>(() => triggers.RecordEventAsync(
                Principal.LocalOwner, ModuleNode.On, "test/pr-opened", "node-id-1", null, Fields, None))).Message,
            (await Assert.ThrowsAsync<DomainException>(() => triggers.RecordEventAsync(
                Principal.LocalOwner, ModuleNode.On, Kind, "node-id-1", null, new Dictionary<string, string> { ["not plain!"] = "x" }, None))).Message,
            (await Assert.ThrowsAsync<DomainException>(() => triggers.RecordEventAsync(
                module, ModuleNode.On, "test/pr-opened", "node-id-1", null, new Dictionary<string, string> { ["not plain!"] = "x" }, None))).Message,
            (await Assert.ThrowsAsync<DomainException>(() => triggers.RecordEventAsync(
                Principal.LocalOwner, ModuleNode.On, "flow-run-ended", "node-id-1", null, Fields, None))).Message,
        ];

        // Switched off after it recorded, the module is refused the event it recorded while it was on.
        await node.SwitchOnAsync(ModuleNode.On);
        DomainException switchedOff = await Assert.ThrowsAsync<DomainException>(
            () => triggers.RecordEventAsync(module, ModuleNode.On, "test/pr-opened", "node-id-1", null, Fields, None));
        Assert.Equal(ModuleNode.NotSwitchedOn(ModuleNode.Module, ModuleNode.On), switchedOff.Message);

        foreach (string refusal in refusals.Append(switchedOff.Message))
        {
            Assert.DoesNotContain(byModule.Id.ToString(), refusal, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(byPerson.Id.ToString(), refusal, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(parcel.ToString(), refusal, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("4711-recorded", refusal, StringComparison.Ordinal);
        }

        Assert.Equal("'event.record' is not open to the Agent role.", refusals[0]);
        Assert.Equal("Only module 'test' records external events of kinds prefixed 'test/'; 'owner' cannot record 'test/pr-opened'.", refusals[1]);
        Assert.StartsWith("An external event's field names are plain names", refusals[2], StringComparison.Ordinal);
        Assert.StartsWith("An external event's field names are plain names", refusals[3], StringComparison.Ordinal);
        Assert.StartsWith("The kind 'flow-run-ended'", refusals[4], StringComparison.Ordinal);
        Assert.Equal(2, (await AppendedAsync(node.Services.GetRequiredService<IDocumentStore>(), ModuleNode.On)).Count);
    }

    [Fact]
    public async Task Recording_an_external_event_of_the_attention_kind_is_refused_whoever_records_it_and_nothing_is_recorded()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        var triggers = new TriggerService(store, TimeProvider.System);

        foreach (Principal recorder in new[] { Principal.LocalOwner, Teammate })
        {
            DomainException refused = await Assert.ThrowsAsync<DomainException>(
                () => triggers.RecordEventAsync(recorder, Alpha, TriggerEvent.AttentionKind, "pause-1", null, Fields, None));
            Assert.Equal("The kind 'attention' is what a flow run's pause reads as; an external event cannot use it.", refused.Message);
        }

        Assert.Empty(await AppendedAsync(store, Alpha));
    }

    [Fact]
    public async Task An_external_id_at_the_cap_is_recorded_and_one_a_character_over_it_is_refused()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        var triggers = new TriggerService(store, TimeProvider.System);
        string atCap = new('x', ExternalEventCaps.ExternalIdLength);

        ExternalEvent recorded = await triggers.RecordEventAsync(Principal.LocalOwner, Alpha, Kind, atCap, null, Fields, None);
        DomainException refused = await Assert.ThrowsAsync<DomainException>(
            () => triggers.RecordEventAsync(Principal.LocalOwner, Alpha, Kind, atCap + "x", null, Fields, None));

        Assert.Equal(atCap, recorded.ExternalId);
        Assert.Equal(
            $"An external event's external id is at most {ExternalEventCaps.ExternalIdLength} characters; this one is {ExternalEventCaps.ExternalIdLength + 1}.",
            refused.Message);
        Assert.Equal([recorded.Id], (await AppendedAsync(store, Alpha)).Select(e => e.EventId));
    }
}
