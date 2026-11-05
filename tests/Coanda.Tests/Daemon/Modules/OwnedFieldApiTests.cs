using System.Net;
using Coanda.Cli;
using Coanda.Cli.Commands;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Registry;
using Coanda.Core.Types;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.Triggers;
using Coanda.Tests.Daemon.FlowRuns;
using Coanda.Tests.Daemon.SystemSteps;
using Fisher;
using JasperFx.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.Modules;

/// <summary>
/// A field a module owns, read from the type version a parcel was created under, is set by that
/// module on any parcel of a project it is switched on for, with no dispatch, and by an Owner-role
/// principal, and every other caller is refused it. Its value shows in parcel show as any other field's.
/// The owned fields are declared by types stand-in modules bring: the test module's probe owns
/// <see cref="Mirror"/>, and another module's relay owns <see cref="Foreign"/>.
/// </summary>
public sealed class OwnedFieldApiTests
{
    private const string On = ModuleNode.On;
    private const string Off = ModuleNode.Off;
    private const string Mirror = "mirror";
    private const string Foreign = "foreign";
    private const string Probe = "probe";
    private const string Relay = "relay";
    private static readonly CancellationToken None = CancellationToken.None;

    private static readonly ParcelType ProbeType = SoftwareTypes.UnownedTask with
    {
        Name = Probe,
        FlowBindings = [],
        Fields = [.. SoftwareTypes.UnownedTask.Fields, new FieldDeclaration { Name = Mirror, Kind = FieldKinds.Text, Owner = ModuleNode.Module }],
    };

    private static readonly ParcelType RelayType = SoftwareTypes.UnownedTask with
    {
        Name = Relay,
        FlowBindings = [],
        Fields = [.. SoftwareTypes.UnownedTask.Fields, new FieldDeclaration { Name = Foreign, Kind = FieldKinds.Text, Owner = ModuleNode.Other }],
    };

    [Fact]
    public async Task The_owning_module_sets_its_field_with_no_dispatch_and_the_value_shows_in_parcel_show()
    {
        await using OwnedNode owned = await StartAsync();
        ModuleNode node = owned.Node;
        Parcel parcel = await node.ParcelAsync(On, "Mirrored", Probe);
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
        await using OwnedNode owned = await StartAsync();
        ModuleNode node = owned.Node;
        Parcel parcel = await node.ParcelAsync(On, "Guarded", Probe);
        ParcelService parcels = node.Services.GetRequiredService<ParcelService>();

        await parcels.SetFieldAsync(Principal.LocalOwner, On, parcel.Id, Mirror, FieldValue.OfText("By the owner."), None);
        await parcels.SetFieldAsync(DefinitionService.ImportPrincipal, On, parcel.Id, Mirror, FieldValue.OfText("By the import."), None);

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
            Assert.StartsWith($"Field '{Mirror}' of a '{Probe}' is owned by module 'test'; it is set by that module and by an Owner-role principal, and the {principal.Role} principal '{principal.Id}'", exception.Message, StringComparison.Ordinal);
        }

        Assert.Equal(["By the owner.", "By the import."], (await EventsAsync(node, On, parcel.Id)).OfType<ParcelFieldSet>().Select(e => e.Value.Text));
    }

    [Fact]
    public async Task Another_module_is_refused_an_owned_field_while_it_holds_a_dispatch_on_the_parcel()
    {
        await using OwnedNode owned = await StartAsync();
        ModuleNode node = owned.Node;
        await Publishing.FlowAsync(node.Daemon, On, RunningSystemStep.FlowNamed("relaying") with { Accepts = [Relay] });
        (Guid parcelId, Guid flowRunId, Guid dispatchId) = await node.DispatchedAsync(On, type: Relay, flow: "relaying");
        string token = await node.TokenAsync(ModuleNode.Module);
        var held = new NamedDispatch(flowRunId, dispatchId);

        string refusal = await node.RefusedAsync(token, HttpMethod.Post, "/parcels/set-field", Set(parcelId, Foreign, "Not mine.", held));

        Assert.Equal(
            $"Field '{Foreign}' of a '{Relay}' is owned by module '{ModuleNode.Other}'; it is set by that module and by an Owner-role principal, and module '{ModuleNode.Module}' is neither.",
            refusal);
        Assert.Equal(HttpStatusCode.OK, (await node.SendAsync(token, HttpMethod.Post, "/parcels/set-field", Set(parcelId, TaskFields.Objective, "Mine to set.", held))).Status);
        Assert.Equal([TaskFields.Objective], (await EventsAsync(node, On, parcelId)).OfType<ParcelFieldSet>().Select(e => e.Field));
    }

    [Fact]
    public async Task The_owning_module_is_refused_its_field_in_a_project_it_is_not_switched_on_for()
    {
        await using OwnedNode owned = await StartAsync();
        ModuleNode node = owned.Node;
        Parcel parcel = await node.ParcelAsync(Off, "Elsewhere", Probe);
        string token = await node.TokenAsync(ModuleNode.Module);

        string refusal = await node.RefusedAsync(token, HttpMethod.Post, "/parcels/set-field", Set(parcel.Id, Mirror, "Not here.", project: Off));

        Assert.Equal(ModuleNode.NotSwitchedOn(ModuleNode.Module, Off), refusal);
        Assert.Empty((await EventsAsync(node, Off, parcel.Id)).OfType<ParcelFieldSet>());
    }

    [Fact]
    public async Task Whether_a_field_is_owned_and_by_whom_is_read_from_the_version_the_parcel_was_created_under()
    {
        await using OwnedNode owned = await StartAsync();
        ModuleNode node = owned.Node;
        Parcel older = await node.ParcelAsync(On, "Under the module's version", Probe);
        ParcelType unowned = ProbeType with { Fields = [.. ProbeType.Fields.Select(f => f with { Owner = null })] };
        await PublishAsync(node, On, unowned, new ForkBase(Probe, 1));
        Parcel newer = await node.ParcelAsync(On, "Under the project's fork", Probe);
        string token = await node.TokenAsync(ModuleNode.Module);

        Assert.NotEqual(older.TypeHash, newer.TypeHash);
        Assert.Equal(HttpStatusCode.OK, (await node.SendAsync(token, HttpMethod.Post, "/parcels/set-field", Set(older.Id, Mirror, "Still mine."))).Status);
        string refusal = await node.RefusedAsync(token, HttpMethod.Post, "/parcels/set-field", Set(newer.Id, Mirror, "No longer mine."));

        Assert.StartsWith($"Module '{ModuleNode.Module}' sets a parcel's field only by naming a dispatch", refusal, StringComparison.Ordinal);
        Assert.Empty((await EventsAsync(node, On, newer.Id)).OfType<ParcelFieldSet>());
    }

    /// <summary>A module node importing the stand-in modules' probe and relay types, both visible in every project.</summary>
    private static async Task<OwnedNode> StartAsync()
    {
        StandInModuleDirectory test = StandInModuleDirectory.Write(ModuleNode.Module, "1.0.0", ModuleDefinition.FromParcelType(ProbeType));
        StandInModuleDirectory other = StandInModuleDirectory.Write(ModuleNode.Other, "1.0.0", ModuleDefinition.FromParcelType(RelayType));
        return new OwnedNode(await ModuleNode.StartAsync(modulePaths: [test.Path, other.Path]), [test, other]);
    }

    private static async Task PublishAsync(ModuleNode node, string project, ParcelType type, ForkBase forkBase)
    {
        DefinitionService definitions = node.Services.GetRequiredService<DefinitionService>();
        Definition draft = await definitions.DraftAsync(
            Principal.LocalOwner, DefinitionKind.ParcelType, DefinitionScope.Project, project, type.Name,
            ParcelType.CanonicalJson(type), type.Hash, forkBase, None);
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

    private sealed class OwnedNode(ModuleNode node, IReadOnlyList<StandInModuleDirectory> modules) : IAsyncDisposable
    {
        public ModuleNode Node { get; } = node;

        public async ValueTask DisposeAsync()
        {
            await Node.DisposeAsync();
            foreach (StandInModuleDirectory module in modules)
            {
                module.Dispose();
            }
        }
    }
}
