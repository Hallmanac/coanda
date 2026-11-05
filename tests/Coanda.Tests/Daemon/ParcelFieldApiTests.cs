using Coanda.Cli;
using Coanda.Cli.Commands;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Registry;
using Coanda.Core.Types;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Store;
using Fisher;
using JasperFx.Events;

namespace Coanda.Tests.Daemon;

/// <summary>The parcel.set-field command through the daemon's endpoint, the parcel view's fields, and the shipped gate on a stock daemon.</summary>
public sealed class ParcelFieldApiTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    private static Task<ParcelView> SetAsync(DaemonClient client, Guid parcelId, string field, FieldValue value) =>
        client.PostAsync<ParcelView>("/parcels/set-field", new ParcelSetFieldRequest(Project, parcelId, field, value), None);

    [Fact]
    public void Set_field_is_open_to_the_roles_move_is_open_to_and_to_the_module_role()
    {
        RegistryEntry setField = CommandRegistry.Entries.Single(e => e.Name == "parcel.set-field");
        RegistryEntry move = CommandRegistry.Entries.Single(e => e.Name == "parcel.move");

        Assert.Equal(EntryKind.Command, setField.Kind);
        Assert.Equal([Role.Module], setField.AllowedRoles.Except(move.AllowedRoles));
        Assert.Empty(move.AllowedRoles.Except(setField.AllowedRoles));
    }

    [Fact]
    public async Task Each_kind_the_cli_builds_is_set_through_the_endpoint_recorded_as_an_event_and_shown_with_every_declared_field()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView added = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Fielded", null), None);

        Assert.Equal([("objective", FieldKinds.Text, null), ("criteria", FieldKinds.TextList, null), ("non-goals", FieldKinds.TextList, null), ("pull-request", FieldKinds.Reference, null)], added.Fields.Select(f => (f.Name, f.Kind, f.Value)));

        await SetAsync(client, added.Id, "objective", new ParcelSetSettings { Id = "x", Field = "objective", Text = "Ship fields." }.ToValue());
        await SetAsync(client, added.Id, "criteria", new ParcelSetSettings { Id = "x", Field = "criteria", Items = ["It builds.", "It tests."] }.ToValue());
        ParcelView set = await SetAsync(
            client, added.Id, "pull-request", new ParcelSetSettings { Id = "x", Field = "pull-request", Reference = "42", Url = "https://example.com/pull/42" }.ToValue());

        ParcelView shown = await client.GetAsync<ParcelView>($"/parcels/{added.Id}?project={Project}", None);
        Assert.Equivalent(set, shown, strict: true);
        Assert.Equivalent(FieldValue.OfText("Ship fields."), shown.Fields.Single(f => f.Name == "objective").Value, strict: true);
        Assert.Equivalent(FieldValue.OfItems("It builds.", "It tests."), shown.Fields.Single(f => f.Name == "criteria").Value, strict: true);
        Assert.Null(shown.Fields.Single(f => f.Name == "non-goals").Value);
        Assert.Equivalent(FieldValue.OfReference("42", "https://example.com/pull/42"), shown.Fields.Single(f => f.Name == "pull-request").Value, strict: true);
        Assert.Equivalent(shown, (await client.GetAsync<List<ParcelView>>($"/parcels?project={Project}", None)).Single(), strict: true);

        Assert.Equal(
            [
                ["objective", "Ship fields."],
                ["criteria", "- It builds.\n- It tests."],
                ["non-goals", "not set"],
                ["pull-request", "42 (https://example.com/pull/42)"],
            ],
            ParcelRendering.FieldRows(shown));

        await daemon.StopAsync(None);

        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        await using IQuerySession session = store.QuerySession(Project);
        IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(added.Id, token: None);
        Assert.Equal(["objective", "criteria", "pull-request"], events.Select(e => e.Data).OfType<ParcelFieldSet>().Select(e => e.Field));
    }

    [Fact]
    public async Task A_refused_set_quotes_the_rule_and_records_nothing()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Refusals", null), None);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => SetAsync(client, parcel.Id, "branch", FieldValue.OfText("main")));
        Assert.StartsWith("Parcel type 'task' declares no field 'branch';", exception.Message, StringComparison.Ordinal);
        DomainException exception2 = await Assert.ThrowsAsync<DomainException>(() => SetAsync(client, parcel.Id, "criteria", FieldValue.OfText("one line")));
        Assert.StartsWith("Field 'criteria' of a 'task' holds a list of text, and the value given is a text;", exception2.Message, StringComparison.Ordinal);
        DomainException exception3 = await Assert.ThrowsAsync<DomainException>(() => SetAsync(client, parcel.Id, "objective", FieldValue.OfText("  ")));
        Assert.StartsWith("The value given for field 'objective' is empty;", exception3.Message, StringComparison.Ordinal);
        DomainException exception4 = await Assert.ThrowsAsync<DomainException>(() => SetAsync(client, parcel.Id, "pull-request", new ParcelSetSettings { Id = "x", Field = "pull-request", Url = "https://example.com" }.ToValue()));
        Assert.StartsWith("The value given for field 'pull-request' is empty;", exception4.Message, StringComparison.Ordinal);

        DomainException exception5 = await Assert.ThrowsAsync<DomainException>(() => client.PostAsync<ParcelView>("/parcels/set-field", new { Project, ParcelId = parcel.Id, Field = "objective" }, None));
        Assert.Equal("No value was given for field 'objective'; a field is set to a value of its own kind.", exception5.Message);

        await client.PostAsync<ParcelView>("/parcels/close", new ParcelCloseRequest(Project, parcel.Id, "abandoned", "Not wanted."), None);
        DomainException exception6 = await Assert.ThrowsAsync<DomainException>(() => SetAsync(client, parcel.Id, "objective", FieldValue.OfText("Too late.")));
        Assert.EndsWith("is closed (abandoned); a closed parcel's fields are set by nobody.", exception6.Message, StringComparison.Ordinal);

        Assert.All((await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", None)).Fields, f => Assert.True(f.Value == null));

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_stock_daemon_runs_the_required_fields_gate_with_no_gate_named_in_its_options()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions(), None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Unbriefed", null), None);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => client.PostAsync<ParcelView>("/parcels/move", new ParcelMoveRequest(Project, parcel.Id, "build"), None));
        Assert.Matches(@"(?s)^The gate 'required-fields' refused the move from 'refine' to 'build': .*'objective', 'criteria' have none\.$", exception.Message);

        ParcelView paused = await client.PostAsync<ParcelView>("/parcels/move", new ParcelMoveRequest(Project, parcel.Id, "paused"), None);
        // pausing from refine is never refused for an empty field
        Assert.Equal("paused", paused.Stage);
        DomainException exception2 = await Assert.ThrowsAsync<DomainException>(() => client.PostAsync<ParcelView>("/parcels/move", new ParcelMoveRequest(Project, parcel.Id, "build"), None));
        Assert.Matches(@"(?s)^.*from 'paused' to 'build'.*resumes into 'build'.*'refine'.*'objective', 'criteria' have none\.$", exception2.Message);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_parcel_under_a_type_version_that_declares_no_fields_shows_none_and_takes_none()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        await RegisteredProjects.AddAsync(store, Project, None);
        var definitions = new DefinitionService(store, TimeProvider.System, systemSteps: ShippedSystemSteps.Names);
        ParcelType before = SoftwareTypes.Task with { Fields = [], GateBindings = [] };
        Definition draft = await definitions.DraftParcelTypeAsync(Principal.LocalOwner, DefinitionScope.Project, Project, before, null, None);
        await definitions.PublishAsync(Principal.LocalOwner, Project, draft.Id, None);
        var parcels = new ParcelService(store, TimeProvider.System);

        Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "From before fields", null, None);

        Assert.Empty(parcel.Fields);
        Assert.Empty(ParcelView.From(parcel, await parcels.TypeVersionAsync(Principal.LocalOwner, Project, parcel.TypeHash, None)).Fields);
        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => parcels.SetFieldAsync(Principal.LocalOwner, Project, parcel.Id, "objective", FieldValue.OfText("x"), None));
        Assert.Matches(@"(?s)^.*declares no field 'objective'.*and it declares none\.$", exception.Message);
        // that version binds no gate
        Assert.Equal("build", (await parcels.MoveAsync(Principal.LocalOwner, Project, parcel.Id, "build", None)).Stage);
    }
}
