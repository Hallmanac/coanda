using System.Net;
using Coanda.Cli;
using Coanda.Cli.Commands;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Registry;
using Coanda.Core.Software;
using Coanda.Core.Types;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Triggers;
using Fisher;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Modules;

/// <summary>
/// A field a module owns, read from the type version a parcel was created under, is set by that
/// module on any parcel of a project it is switched on for, with no dispatch, and by an Owner-role
/// principal, and every other caller is refused it. Its value shows in parcel show as any other field's.
/// </summary>
public sealed class OwnedFieldApiTests
{
    private const string On = ModuleNode.On;
    private const string Off = ModuleNode.Off;
    private const string Mirror = "mirror";
    private const string Foreign = "foreign";
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task The_owning_module_sets_its_field_with_no_dispatch_and_the_value_shows_in_parcel_show()
    {
        await using ModuleNode node = await StartAsync();
        Parcel parcel = await node.ParcelAsync(On, "Mirrored");
        string token = await node.TokenAsync(ModuleNode.Module);

        (HttpStatusCode status, string body) = await node.SendAsync(token, HttpMethod.Post, "/parcels/set-field", Set(parcel.Id, Mirror, "From outside."));

        Assert.True(status == HttpStatusCode.OK, $"The owning module's set-field was answered {status}: {body}");
        using var owner = new DaemonClient(node.Home);
        ParcelView shown = await owner.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={On}", None);
        Assert.Equivalent(FieldValue.OfText("From outside."), shown.Fields.Single(f => f.Name == Mirror).Value, strict: true);
        Assert.Contains(["mirror", "From outside."], ParcelRendering.FieldRows(shown));
        using var module = new DaemonClient(node.Home, token);
        ParcelView read = await module.GetAsync<ParcelView>($"/parcels/{parcel.Id}?project={On}", None);
        Assert.Equivalent(shown.Fields, read.Fields, strict: true);
        ParcelFieldSet set = Assert.Single((await EventsAsync(node, On, parcel.Id)).OfType<ParcelFieldSet>());
        Assert.Equal("module:test", set.SetBy);
    }

    [Fact]
    public async Task The_owner_sets_an_owned_field_and_every_other_principal_is_refused_it()
    {
        await using ModuleNode node = await StartAsync();
        Parcel parcel = await node.ParcelAsync(On, "Guarded");
        ParcelService parcels = node.Services.GetRequiredService<ParcelService>();

        await parcels.SetFieldAsync(Principal.LocalOwner, On, parcel.Id, Mirror, FieldValue.OfText("By the owner."), None);
        await parcels.SetFieldAsync(DefinitionService.SeedPrincipal, On, parcel.Id, Mirror, FieldValue.OfText("By the seed."), None);

        Principal[] refused =
        [
            new("ana", Role.Member),
            Principal.ForSession(new SessionBinding(On, Guid.NewGuid(), Guid.NewGuid())),
            FlowRunAdvancer.ScanPrincipal,
            TriggerLoop.LoopPrincipal,
        ];
        foreach (Principal principal in refused)
        {
            DomainException exception = await Assert.ThrowsAsync<DomainException>(
                () => parcels.SetFieldAsync(principal, On, parcel.Id, Mirror, FieldValue.OfText("Not mine."), None));
            Assert.StartsWith($"Field '{Mirror}' of a 'task' is owned by module 'test'; it is set by that module and by an Owner-role principal, and the {principal.Role} principal '{principal.Id}'", exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(["By the owner.", "By the seed."], (await EventsAsync(node, On, parcel.Id)).OfType<ParcelFieldSet>().Select(e => e.Value.Text));
    }

    [Fact]
    public async Task Another_module_is_refused_an_owned_field_while_it_holds_a_dispatch_on_the_parcel()
    {
        await using ModuleNode node = await StartAsync();
        (Guid parcelId, Guid flowRunId, Guid dispatchId) = await node.DispatchedAsync(On);
        string token = await node.TokenAsync(ModuleNode.Module);
        var held = new NamedDispatch(flowRunId, dispatchId);

        string refusal = await node.RefusedAsync(token, HttpMethod.Post, "/parcels/set-field", Set(parcelId, Foreign, "Not mine.", held));

        Assert.Equal(
            $"Field '{Foreign}' of a 'task' is owned by module '{ModuleNode.Other}'; it is set by that module and by an Owner-role principal, and module '{ModuleNode.Module}' is neither.",
            refusal);
        Assert.Equal(HttpStatusCode.OK, (await node.SendAsync(token, HttpMethod.Post, "/parcels/set-field", Set(parcelId, Mirror, "Mine.", held))).Status);
        Assert.Equal([Mirror], (await EventsAsync(node, On, parcelId)).OfType<ParcelFieldSet>().Select(e => e.Field));
    }

    [Fact]
    public async Task The_owning_module_is_refused_its_field_in_a_project_it_is_not_switched_on_for()
    {
        await using ModuleNode node = await StartAsync();
        Parcel parcel = await node.ParcelAsync(Off, "Elsewhere");
        string token = await node.TokenAsync(ModuleNode.Module);

        string refusal = await node.RefusedAsync(token, HttpMethod.Post, "/parcels/set-field", Set(parcel.Id, Mirror, "Not here.", project: Off));

        Assert.Equal(ModuleNode.NotSwitchedOn(ModuleNode.Module, Off), refusal);
        Assert.Empty((await EventsAsync(node, Off, parcel.Id)).OfType<ParcelFieldSet>());
    }

    [Fact]
    public async Task Whether_a_field_is_owned_and_by_whom_is_read_from_the_version_the_parcel_was_created_under()
    {
        await using ModuleNode node = await StartAsync();
        Parcel older = await node.ParcelAsync(On, "Under the first version");
        await PublishAsync(node, On, TypeWith(mirrorOwner: ModuleNode.Other));
        Parcel newer = await node.ParcelAsync(On, "Under the second version");
        string token = await node.TokenAsync(ModuleNode.Module);

        Assert.NotEqual(older.TypeHash, newer.TypeHash);
        Assert.Equal(HttpStatusCode.OK, (await node.SendAsync(token, HttpMethod.Post, "/parcels/set-field", Set(older.Id, Mirror, "Still mine."))).Status);
        string refusal = await node.RefusedAsync(token, HttpMethod.Post, "/parcels/set-field", Set(newer.Id, Mirror, "No longer mine."));

        Assert.StartsWith($"Field '{Mirror}' of a 'task' is owned by module '{ModuleNode.Other}';", refusal, StringComparison.Ordinal);
        Assert.Empty((await EventsAsync(node, On, newer.Id)).OfType<ParcelFieldSet>());
    }

    /// <summary>A module node whose projects both hold a fork of the task type with a field the test module owns and one another module owns.</summary>
    private static async Task<ModuleNode> StartAsync()
    {
        ModuleNode node = await ModuleNode.StartAsync();
        foreach (string project in new[] { On, Off })
        {
            await PublishAsync(node, project, TypeWith(mirrorOwner: ModuleNode.Module));
        }

        return node;
    }

    private static ParcelType TypeWith(string mirrorOwner) => ShippedTypes.Task with
    {
        Fields =
        [
            .. ShippedTypes.Task.Fields,
            new FieldDeclaration { Name = Mirror, Kind = FieldKinds.Text, Owner = mirrorOwner },
            new FieldDeclaration { Name = Foreign, Kind = FieldKinds.Text, Owner = ModuleNode.Other },
        ],
    };

    private static async Task PublishAsync(ModuleNode node, string project, ParcelType type)
    {
        DefinitionService definitions = node.Services.GetRequiredService<DefinitionService>();
        Definition draft = await definitions.DraftAsync(
            Principal.LocalOwner, DefinitionKind.ParcelType, DefinitionScope.Project, project, type.Name,
            ParcelType.CanonicalJson(type), type.Hash, null, None);
        await definitions.PublishAsync(Principal.LocalOwner, project, draft.Id, None);
    }

    private static ParcelSetFieldRequest Set(Guid parcelId, string field, string text, NamedDispatch? dispatch = null, string project = On) =>
        new(project, parcelId, field, FieldValue.OfText(text), dispatch);

    private static async Task<IReadOnlyList<object>> EventsAsync(ModuleNode node, string project, Guid streamId)
    {
        await using IQuerySession session = node.Services.GetRequiredService<IDocumentStore>().QuerySession(project);
        IReadOnlyList<IEvent> events = await session.Events.FetchStreamAsync(streamId, token: None);
        return [.. events.Select(e => e.Data)];
    }
}
