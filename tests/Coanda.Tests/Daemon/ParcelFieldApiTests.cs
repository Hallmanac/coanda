using Coanda.Cli;
using Coanda.Cli.Commands;
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
    public void Set_field_is_open_to_the_same_roles_as_move()
    {
        RegistryEntry setField = CommandRegistry.Entries.Single(e => e.Name == "parcel.set-field");
        RegistryEntry move = CommandRegistry.Entries.Single(e => e.Name == "parcel.move");

        setField.Kind.Should().Be(EntryKind.Command);
        setField.AllowedRoles.Should().BeEquivalentTo(move.AllowedRoles);
    }

    [Fact]
    public async Task Each_kind_the_cli_builds_is_set_through_the_endpoint_recorded_as_an_event_and_shown_with_every_declared_field()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView added = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Fielded", null), None);

        added.Fields.Select(f => (f.Name, f.Kind, f.Value)).Should().Equal(
            ("objective", FieldKinds.Text, null),
            ("criteria", FieldKinds.TextList, null),
            ("non-goals", FieldKinds.TextList, null),
            ("pull-request", FieldKinds.Reference, null));

        await SetAsync(client, added.Id, "objective", new ParcelSetSettings { Id = "x", Field = "objective", Text = "Ship fields." }.ToValue());
        await SetAsync(client, added.Id, "criteria", new ParcelSetSettings { Id = "x", Field = "criteria", Items = ["It builds.", "It tests."] }.ToValue());
        ParcelView set = await SetAsync(
            client, added.Id, "pull-request", new ParcelSetSettings { Id = "x", Field = "pull-request", Reference = "42", Url = "https://example.com/pull/42" }.ToValue());

        ParcelView shown = await client.GetAsync<ParcelView>($"/parcels/{added.Id}?project={Project}", None);
        shown.Should().BeEquivalentTo(set);
        shown.Fields.Single(f => f.Name == "objective").Value.Should().BeEquivalentTo(FieldValue.OfText("Ship fields."));
        shown.Fields.Single(f => f.Name == "criteria").Value.Should().BeEquivalentTo(FieldValue.OfItems("It builds.", "It tests."));
        shown.Fields.Single(f => f.Name == "non-goals").Value.Should().BeNull();
        shown.Fields.Single(f => f.Name == "pull-request").Value.Should().BeEquivalentTo(FieldValue.OfReference("42", "https://example.com/pull/42"));
        (await client.GetAsync<List<ParcelView>>($"/parcels?project={Project}", None)).Single().Should().BeEquivalentTo(shown);

        ParcelRendering.FieldRows(shown).Should().BeEquivalentTo(
            new[]
            {
                new[] { "objective", "Ship fields." },
                new[] { "criteria", "- It builds.\n- It tests." },
                new[] { "non-goals", "not set" },
                new[] { "pull-request", "42 (https://example.com/pull/42)" },
            },
            o => o.WithStrictOrdering());

        await daemon.StopAsync(None);

        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        await using IQuerySession session = store.QuerySession(Project);
        IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(added.Id, token: None);
        events.Select(e => e.Data).OfType<ParcelFieldSet>().Select(e => e.Field).Should().Equal("objective", "criteria", "pull-request");
    }

    [Fact]
    public async Task A_refused_set_quotes_the_rule_and_records_nothing()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Refusals", null), None);

        await FluentActions.Awaiting(() => SetAsync(client, parcel.Id, "branch", FieldValue.OfText("main")))
            .Should().ThrowAsync<DomainException>().WithMessage("Parcel type 'task' declares no field 'branch';*");
        await FluentActions.Awaiting(() => SetAsync(client, parcel.Id, "criteria", FieldValue.OfText("one line")))
            .Should().ThrowAsync<DomainException>().WithMessage("Field 'criteria' of a 'task' holds a list of text, and the value given is a text;*");
        await FluentActions.Awaiting(() => SetAsync(client, parcel.Id, "objective", FieldValue.OfText("  ")))
            .Should().ThrowAsync<DomainException>().WithMessage("The value given for field 'objective' is empty;*");
        await FluentActions.Awaiting(() => SetAsync(client, parcel.Id, "pull-request", new ParcelSetSettings { Id = "x", Field = "pull-request", Url = "https://example.com" }.ToValue()))
            .Should().ThrowAsync<DomainException>().WithMessage("The value given for field 'pull-request' is empty;*");

        await FluentActions.Awaiting(() => client.PostAsync<ParcelView>("/parcels/set-field", new { Project, ParcelId = parcel.Id, Field = "objective" }, None))
            .Should().ThrowAsync<DomainException>().WithMessage("No value was given for field 'objective'; a field is set to a value of its own kind.");

        await client.PostAsync<ParcelView>("/parcels/close", new ParcelCloseRequest(Project, parcel.Id, "abandoned", "Not wanted."), None);
        await FluentActions.Awaiting(() => SetAsync(client, parcel.Id, "objective", FieldValue.OfText("Too late.")))
            .Should().ThrowAsync<DomainException>().WithMessage("*is closed (abandoned); a closed parcel's fields are set by nobody.");

        (await client.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={Project}", None)).Fields.Should().OnlyContain(f => f.Value == null);

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

        await FluentActions.Awaiting(() => client.PostAsync<ParcelView>("/parcels/move", new ParcelMoveRequest(Project, parcel.Id, "build"), None))
            .Should().ThrowAsync<DomainException>()
            .WithMessage("The gate 'required-fields' refused the move from 'refine' to 'build': *'objective', 'criteria' have none.");

        ParcelView paused = await client.PostAsync<ParcelView>("/parcels/move", new ParcelMoveRequest(Project, parcel.Id, "paused"), None);
        paused.Stage.Should().Be("paused", "pausing from refine is never refused for an empty field");
        await FluentActions.Awaiting(() => client.PostAsync<ParcelView>("/parcels/move", new ParcelMoveRequest(Project, parcel.Id, "build"), None))
            .Should().ThrowAsync<DomainException>().WithMessage("*from 'paused' to 'build'*resumes into 'build'*'refine'*'objective', 'criteria' have none.");

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_parcel_under_a_type_version_that_declares_no_fields_shows_none_and_takes_none()
    {
        using var temp = new TemporaryHome();
        await using IDocumentStore store = await SeededStore.OpenAsync(temp.Home.DatabasePath);
        await RegisteredProjects.AddAsync(store, Project, None);
        var definitions = new DefinitionService(store, TimeProvider.System, systemSteps: ShippedSystemSteps.Names);
        ParcelType before = ShippedTypes.Task with { Fields = [], GateBindings = [] };
        Definition draft = await definitions.DraftParcelTypeAsync(Principal.LocalOwner, DefinitionScope.Project, Project, before, null, None);
        await definitions.PublishAsync(Principal.LocalOwner, Project, draft.Id, None);
        var parcels = new ParcelService(store, TimeProvider.System);

        Parcel parcel = await parcels.AddAsync(Principal.LocalOwner, Project, "task", "From before fields", null, None);

        parcel.Fields.Should().BeEmpty();
        ParcelView.From(parcel, await parcels.TypeVersionAsync(Principal.LocalOwner, Project, parcel.TypeHash, None)).Fields.Should().BeEmpty();
        await FluentActions.Awaiting(() => parcels.SetFieldAsync(Principal.LocalOwner, Project, parcel.Id, "objective", FieldValue.OfText("x"), None))
            .Should().ThrowAsync<DomainException>().WithMessage("*declares no field 'objective'*and it declares none.");
        (await parcels.MoveAsync(Principal.LocalOwner, Project, parcel.Id, "build", None)).Stage.Should().Be("build", "that version binds no gate");
    }
}
