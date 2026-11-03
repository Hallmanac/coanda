using System.Text.Json;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.Flights;
using Coanda.Core.Flights.Events;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Core.Registry;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Store;
using Fisher;
using JasperFx;
using JasperFx.Events.Documents;

namespace Coanda.Daemon.Flights;

/// <summary>
/// The daemon's side of every flight command and query: pass the registry for the method's own
/// entry, fetch for writing, ask <see cref="FlightDecider"/>, append, save. A new flight takes the
/// flow that resolution returns for the project by name; an existing flight keeps the version
/// whose hash it recorded.
/// </summary>
/// <remarks>
/// A parcel has at most one live flight, and that holds when two starts race: a start appends
/// <see cref="ParcelFlightAttached"/> to the parcel stream in the same unit of work that starts the
/// flight stream, through <c>FetchForWriting</c>, so the parcel stream's expected version guards it.
/// The loser of a race fails the version check, its flight stream is never written, and it is
/// refused with the rule. Ending and stopping append <see cref="ParcelFlightReleased"/> the same way.
/// </remarks>
public sealed class FlightService(IDocumentStore store, TimeProvider clock)
{
    private readonly DefinitionResolver definitions = new(store);

    public async Task<Flight> StartAsync(Principal principal, string project, Guid parcelId, string flowName, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("flight.start", principal);
        string tenant = Tenants.RequireProject(project);
        Definition published = await definitions.ResolveAsync(tenant, DefinitionKind.Flow, flowName, cancellationToken)
            ?? throw new DomainException($"'{flowName}' is not a published flow of project '{tenant}'; coa flow list names the ones that are.");
        FlowDefinition flow = FlowDefinition.FromJson(published.Content);

        await using IDocumentSession session = store.LightweightSession(tenant);
        var parcelStream = await session.Events.FetchForWriting<Parcel>(parcelId, cancellationToken);
        Parcel parcel = parcelStream.Aggregate ?? throw ParcelNotFound(parcelId, tenant);
        (FlightStarted started, ParcelFlightAttached attached) =
            FlightDecider.Start(flow, published.VersionNumber, parcel, principal.Id, clock.GetUtcNow());
        parcelStream.AppendOne(attached);
        session.Events.StartStream<Flight>(started.FlightId, started);
        await SaveGuardedAsync(session, ParcelChanged(parcel.Id), cancellationToken);

        return Flight.Create(started);
    }

    public Task<Flight> StartStepAsync(Principal principal, string project, Guid flightId, string step, CancellationToken cancellationToken) =>
        WriteAsync(
            "flight.step", principal, project, flightId,
            (flow, flight) => FlightDecider.StartStep(flow, flight, step, clock.GetUtcNow()),
            (flight, started) => flight.Apply(started),
            cancellationToken);

    public Task<Flight> RecordAnswerAsync(
        Principal principal,
        string project,
        Guid flightId,
        string step,
        string agent,
        string agentHash,
        ModelTriple model,
        JsonElement answer,
        Measurement measurement,
        CancellationToken cancellationToken) =>
        WriteAsync(
            "flight.answer", principal, project, flightId,
            (flow, flight) => FlightDecider.Answer(flow, flight, step, agent, agentHash, model, answer, measurement, clock.GetUtcNow()),
            (flight, answered) => flight.Apply(answered),
            cancellationToken);

    public Task<Flight> RecordResultAsync(
        Principal principal, string project, Guid flightId, string step, string action, JsonElement result, CancellationToken cancellationToken) =>
        WriteAsync(
            "flight.result", principal, project, flightId,
            (flow, flight) => FlightDecider.Complete(flow, flight, step, action, result, clock.GetUtcNow()),
            (flight, completed) => flight.Apply(completed),
            cancellationToken);

    public Task<Flight> ParkAsync(Principal principal, string project, Guid flightId, string reason, string lever, CancellationToken cancellationToken) =>
        WriteAsync(
            "flight.park", principal, project, flightId,
            (flow, flight) => FlightDecider.Park(flow, flight, reason, lever, principal.Id, clock.GetUtcNow()),
            (flight, parked) => flight.Apply(parked),
            cancellationToken);

    public Task<Flight> ResumeAsync(Principal principal, string project, Guid flightId, string? note, CancellationToken cancellationToken) =>
        WriteAsync(
            "flight.resume", principal, project, flightId,
            (flow, flight) => FlightDecider.Resume(flow, flight, note, principal.Id, clock.GetUtcNow()),
            (flight, resumed) => flight.Apply(resumed),
            cancellationToken);

    public async Task<Flight> EndAsync(Principal principal, string project, Guid flightId, string outcome, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("flight.end", principal);
        string tenant = Tenants.RequireProject(project);

        await using IDocumentSession session = store.LightweightSession(tenant);
        var flightStream = await session.Events.FetchForWriting<Flight>(flightId, cancellationToken);
        Flight flight = flightStream.Aggregate ?? throw FlightNotFound(flightId, tenant);
        FlowDefinition flow = await RequireStartedUnderFlowAsync(tenant, flight, cancellationToken);
        (FlightEnded ended, ParcelFlightReleased released) = FlightDecider.End(flow, flight, outcome, principal.Id, clock.GetUtcNow());
        flightStream.AppendOne(ended);
        await ReleaseAsync(session, flight, released, cancellationToken);

        flight.Apply(ended);
        return flight;
    }

    /// <summary>Stops a live flight. It needs no flow, so a flight whose flow is no longer held can still be stopped.</summary>
    public async Task<Flight> StopAsync(Principal principal, string project, Guid flightId, string reason, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("flight.stop", principal);
        string tenant = Tenants.RequireProject(project);

        await using IDocumentSession session = store.LightweightSession(tenant);
        var flightStream = await session.Events.FetchForWriting<Flight>(flightId, cancellationToken);
        Flight flight = flightStream.Aggregate ?? throw FlightNotFound(flightId, tenant);
        (FlightStopped stopped, ParcelFlightReleased released) = FlightDecider.Stop(flight, reason, principal.Id, clock.GetUtcNow());
        flightStream.AppendOne(stopped);
        await ReleaseAsync(session, flight, released, cancellationToken);

        flight.Apply(stopped);
        return flight;
    }

    /// <summary>The project's flights, or one parcel's when <paramref name="parcelId"/> is given, oldest first.</summary>
    public async Task<IReadOnlyList<Flight>> ListAsync(Principal principal, string project, Guid? parcelId, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("flight.list", principal);
        string tenant = Tenants.RequireProject(project);

        await using IQuerySession session = store.QuerySession(tenant);
        IReadOnlyList<Flight> flights = parcelId is Guid parcel
            ? await session.Query<Flight>().Where(f => f.ParcelId == parcel).ToListAsync(cancellationToken)
            : await session.Query<Flight>().ToListAsync(cancellationToken);
        return [.. flights.OrderBy(f => f.StartedAt).ThenBy(f => f.Id)];
    }

    public async Task<Flight?> ShowAsync(Principal principal, string project, Guid flightId, CancellationToken cancellationToken)
    {
        CommandRegistry.Require("flight.show", principal);
        string tenant = Tenants.RequireProject(project);

        await using IQuerySession session = store.QuerySession(tenant);
        return await session.LoadAsync<Flight>(flightId, cancellationToken);
    }

    private async Task<Flight> WriteAsync<TEvent>(
        string entry,
        Principal principal,
        string project,
        Guid flightId,
        Func<FlowDefinition, Flight, TEvent> decide,
        Action<Flight, TEvent> apply,
        CancellationToken cancellationToken)
        where TEvent : notnull
    {
        CommandRegistry.Require(entry, principal);
        string tenant = Tenants.RequireProject(project);

        await using IDocumentSession session = store.LightweightSession(tenant);
        var stream = await session.Events.FetchForWriting<Flight>(flightId, cancellationToken);
        Flight flight = stream.Aggregate ?? throw FlightNotFound(flightId, tenant);
        FlowDefinition flow = await RequireStartedUnderFlowAsync(tenant, flight, cancellationToken);
        TEvent recorded = decide(flow, flight);
        stream.AppendOne(recorded);
        await SaveGuardedAsync(
            session,
            $"Flight {Ids.Short(flight.Id)} changed while this was being recorded, so nothing was. Read the flight again and retry.",
            cancellationToken);

        apply(flight, recorded);
        return flight;
    }

    private static async Task ReleaseAsync(IDocumentSession session, Flight flight, ParcelFlightReleased released, CancellationToken cancellationToken)
    {
        var parcelStream = await session.Events.FetchForWriting<Parcel>(flight.ParcelId, cancellationToken);
        Parcel parcel = parcelStream.Aggregate ?? throw ParcelNotFound(flight.ParcelId, session.TenantId);

        // A parcel names only its live flight; releasing one it does not name would clear another's hold.
        if (parcel.LiveFlightId == flight.Id)
        {
            parcelStream.AppendOne(released);
        }

        await SaveGuardedAsync(session, ParcelChanged(parcel.Id), cancellationToken);
    }

    /// <summary>Saves, turning a failed version check into a refusal that says what moved.</summary>
    private static async Task SaveGuardedAsync(IDocumentSession session, string refusal, CancellationToken cancellationToken)
    {
        try
        {
            await session.SaveChangesAsync(cancellationToken);
        }
        catch (ConcurrencyException)
        {
            throw new DomainException(refusal);
        }
    }

    private static string ParcelChanged(Guid parcelId) =>
        $"A parcel has at most one live flight; parcel {Ids.Short(parcelId)} or its flight changed while this was being recorded, so nothing was. Read them again and retry.";

    /// <summary>A flight keeps the flow version it started under; a newer version applies to new flights only.</summary>
    private async Task<FlowDefinition> RequireStartedUnderFlowAsync(string project, Flight flight, CancellationToken cancellationToken)
    {
        Definition? found = await definitions.ResolveByHashAsync(project, DefinitionKind.Flow, flight.FlowHash, cancellationToken);
        return found is null
            ? throw new DomainException($"The '{flight.FlowName}' flow version {flight.FlowHash} that flight {Ids.Short(flight.Id)} started under is no longer held.")
            : FlowDefinition.FromJson(found.Content);
    }

    private static DomainException ParcelNotFound(Guid parcelId, string project) =>
        new($"No parcel {Ids.Short(parcelId)} in project '{project}'.");

    private static DomainException FlightNotFound(Guid flightId, string project) =>
        new($"No flight {Ids.Short(flightId)} in project '{project}'.");
}
