using System.Text.Json;
using System.Text.Json.Nodes;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.FlowRuns;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Core.Types;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;

namespace Coanda.Daemon.SystemSteps;

/// <summary>
/// Creates parcels from the list an earlier step of the flow answered. The <c>from</c> argument names that
/// step; its latest recorded answer on the flow run holds a <c>parcels</c> array of
/// <c>{ "type": "...", "title": "..." }</c> entries, and each entry becomes one parcel of that type in the
/// project, at its type's first entry stage, whose stream records a derived-from relation to the flow run's
/// parcel naming the flow run in the save that creates it. Every entry is checked before any is created: its
/// type is one the type version the flow run's parcel recorded may derive, and one the project resolves, and
/// its title is one the parcel decider accepts; an entry refused pauses the flow run and creates nothing.
/// <para>
/// Each derived parcel records an origin naming the answer it came from (<see cref="OriginOf"/>), so
/// performing the step again on an answer already derived from, after a restart or a resume, reuses the
/// parcels already created and creates only the missing ones. The parcels are created as a Member-role
/// principal named for the flow run (<see cref="PrincipalFor"/>), since <c>parcel.add</c> is open to people
/// only; every read is made as the flow run's own principal. Neither the principal's id nor an origin
/// carries any text from the answer.
/// </para>
/// </summary>
public sealed class DeriveParcelsSystemStep(ParcelService parcels, FlowRunService flowRuns) : IFlowSystemStep
{
    public const string FromArgument = "from";

    /// <summary>The answer's member that lists the parcels to derive.</summary>
    public const string ParcelsMember = "parcels";

    /// <summary>The result's member that lists the derived parcels' ids.</summary>
    public const string ParcelIdsMember = "parcelIds";

    /// <summary>The most entries one answer may list.</summary>
    public const int MaxEntries = 50;

    public string Name => CoreSystemSteps.DeriveParcels;

    /// <summary>The Member-role principal that creates the parcels the flow run <paramref name="flowRunId"/> derives.</summary>
    public static Principal PrincipalFor(Guid flowRunId) => new($"derive-parcels:{flowRunId}", Role.Member);

    /// <summary>
    /// The origin of the parcel derived from the entry at 1-based <paramref name="position"/> of the answer that
    /// visit <paramref name="visit"/> of step <paramref name="from"/> recorded on the flow run <paramref name="flowRunId"/>.
    /// </summary>
    public static string OriginOf(Guid flowRunId, string from, int visit, int position) =>
        $"derive-parcels:flow-run:{flowRunId}:step:{from}:visit:{visit}:entry:{position}";

    public async ValueTask<SystemStepAnswer> DispatchAsync(SystemStepDispatch request, CancellationToken cancellationToken)
    {
        string from = SystemStepArguments.RequireString(request.Step, FromArgument);
        FlowRun flowRun = await flowRuns.ShowAsync(request.Principal, request.Project, request.FlowRunId, cancellationToken)
            ?? throw new DomainException($"No flow run {Ids.Short(request.FlowRunId)} in project '{request.Project}'.");

        if (LatestAnswer(flowRun, from) is not (JsonElement answer, int visit))
        {
            return Paused(request, from, $"step '{from}' has recorded no answer on flow run {Ids.Short(request.FlowRunId)}.");
        }

        if (answer.ValueKind != JsonValueKind.Object
            || !answer.TryGetProperty(ParcelsMember, out JsonElement listed)
            || listed.ValueKind != JsonValueKind.Array)
        {
            return Paused(request, from, $"the answer of step '{from}' has no '{ParcelsMember}' array.");
        }

        int count = listed.GetArrayLength();
        if (count > MaxEntries)
        {
            return Paused(request, from, $"the answer of step '{from}' lists {count} parcels, and at most {MaxEntries} are derived from one answer.");
        }

        Parcel parcel = await parcels.ShowAsync(request.Principal, request.Project, request.ParcelId, cancellationToken)
            ?? throw new DomainException($"No parcel {Ids.Short(request.ParcelId)} in project '{request.Project}'.");
        ParcelType parcelType = await parcels.TypeVersionAsync(request.Principal, request.Project, parcel.TypeHash, cancellationToken)
            ?? throw new DomainException(
                $"Project '{request.Project}' holds no version {parcel.TypeHash} of parcel type '{parcel.TypeName}', which parcel {Ids.Short(parcel.Id)} recorded.");

        List<(ParcelType Type, string Title)> entries = [];
        int position = 0;
        foreach (JsonElement entry in listed.EnumerateArray())
        {
            position++;
            (ParcelType? type, string? title, string? refusal, string? lever) = await CheckAsync(request, parcelType, entry, cancellationToken);
            if (refusal is not null || type is null || title is null)
            {
                return Paused(request, from, $"entry {position} of the answer of step '{from}' {refusal}", lever);
            }

            entries.Add((type, title));
        }

        Principal creator = PrincipalFor(request.FlowRunId);
        var derivedFrom = new DerivedFrom(request.ParcelId, request.FlowRunId);
        JsonArray ids = [];
        position = 0;
        foreach ((ParcelType type, string title) in entries)
        {
            position++;
            string origin = OriginOf(request.FlowRunId, from, visit, position);
            Parcel derived = await parcels.FindByOriginAsync(request.Principal, request.Project, origin, cancellationToken)
                ?? await parcels.AddAsync(creator, request.Project, type.Name, title, null, origin, derivedFrom, cancellationToken);
            ids.Add(derived.Id.ToString());
        }

        return SystemStepAnswer.DoneWith(Name, (FromArgument, from), (ParcelIdsMember, ids));
    }

    /// <summary>
    /// The answer recorded by the latest visit of step <paramref name="from"/> that recorded one, with that
    /// visit's number counting from 1, or null when no visit of the step has recorded an answer. A system
    /// step's result is not an answer.
    /// </summary>
    private static (JsonElement Answer, int Visit)? LatestAnswer(FlowRun flowRun, string from)
    {
        (JsonElement Answer, int Visit)? latest = null;
        int visit = 0;
        foreach (FlowRunStepEntry entry in flowRun.Steps.Where(s => string.Equals(s.Step, from, StringComparison.Ordinal)))
        {
            visit++;
            if (entry.Answer is JsonElement answer)
            {
                latest = (answer, visit);
            }
        }

        return latest;
    }

    /// <summary>
    /// The type and the trimmed title an entry derives, or what refuses it: an entry that is not an object
    /// with a string <c>type</c> and a string <c>title</c>, a type <paramref name="parcelType"/> does not list
    /// as one it may derive or the project does not resolve, or a title the parcel decider refuses. When
    /// <paramref name="parcelType"/> is an earlier version of a type whose version the project holds now may
    /// derive the entry's type, the refusal says so, since a parcel keeps the type version it was created under,
    /// and it carries the lever <see cref="FlowRunLevers.TypeVersionKept"/> in place of a resume no change can
    /// make succeed.
    /// </summary>
    private async Task<(ParcelType? Type, string? Title, string? Refusal, string? Lever)> CheckAsync(
        SystemStepDispatch request, ParcelType parcelType, JsonElement entry, CancellationToken cancellationToken)
    {
        if (entry.ValueKind != JsonValueKind.Object)
        {
            return (null, null, "is not an object.", null);
        }

        if (!entry.TryGetProperty("type", out JsonElement typeValue) || typeValue.ValueKind != JsonValueKind.String)
        {
            return (null, null, "has no string 'type'.", null);
        }

        if (!entry.TryGetProperty("title", out JsonElement titleValue) || titleValue.ValueKind != JsonValueKind.String)
        {
            return (null, null, "has no string 'title'.", null);
        }

        string typeName = typeValue.GetString() ?? string.Empty;
        if (!MayDerive(parcelType, typeName))
        {
            string derives = parcelType.Derives.Count == 0
                ? "declares no type it may derive"
                : $"may derive only {string.Join(", ", parcelType.Derives)}";
            ParcelType? current = await parcels.ResolveTypeAsync(request.Principal, request.Project, parcelType.Name, cancellationToken);
            string refusal = $"names the type '{typeName}', which the flow run's parcel's type '{parcelType.Name}' does not list as one it may derive; it {derives}.";
            return current is not null && current.Hash != parcelType.Hash && MayDerive(current, typeName)
                ? (null, null,
                    $"{refusal} The parcel was created under version {parcelType.Hash} of its type and keeps that version, so the version {current.Hash} the project holds now, which may derive '{typeName}', does not apply to it; resuming cannot derive from this parcel.",
                    FlowRunLevers.TypeVersionKept(parcelType.Name))
                : (null, null, refusal, null);
        }

        ParcelType? type = await parcels.ResolveTypeAsync(request.Principal, request.Project, typeName, cancellationToken);
        if (type is null)
        {
            return (null, null, $"names the type '{typeName}', which project '{request.Project}' resolves no parcel type of.", null);
        }

        try
        {
            return (type, ParcelDecider.RequireTitle(titleValue.GetString()), null, null);
        }
        catch (DomainException refused)
        {
            return (null, null, $"has a title the parcel decider refuses: {refused.Message}", null);
        }
    }

    private static bool MayDerive(ParcelType parcelType, string typeName) =>
        parcelType.Derives.Any(derived => DefinitionResolution.SameName(derived, typeName));

    private static SystemStepAnswer.Paused Paused(SystemStepDispatch request, string from, string reason, string? lever = null) =>
        new($"Step '{request.Step.Name}' could not derive parcels: {reason}", lever ?? FlowRunLevers.AnswerReadAgain(request.Step.Name, from));
}
