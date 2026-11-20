using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Coanda.Core.Definitions;
using Coanda.Core.Flows;
using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;

namespace Coanda.Daemon.Executors;

/// <summary>
/// Builds the per-parcel context body a step's spawn carries. It reads only the parcel record, the
/// step, the visits of the flow run the step runs on, and what the step's other sources read
/// (<see cref="SourceReads"/>); it never takes the agent definition, so the persona reaches the
/// executor only through the definition on the spawn and is never folded into the context. It opens
/// with <see cref="Fences.Framing"/> and a blank line; six fixed lines come next; after them, one
/// entry per field the step declares, in the step's order, holding the field's value or "not set";
/// after those, each source the step declares, in the step's order: a step of the same flow on one
/// line holding its latest recorded visit on this flow run or saying it has not run on this flow run
/// yet; a step of a named flow on one line holding its latest recording on this parcel or saying none
/// was made; the open parcels of a named type; and the parcel's origin. Each value is folded onto its
/// own line, so a title, a field value, or a recorded answer holding a line break cannot start a
/// labelled line of its own, and every field entry begins with <see cref="FieldLabel"/> and every
/// source line with <see cref="SourceLabel"/>, or with a hyphen, after any number of spaces, under
/// such a line, so none reads as one of the fixed labels. The title, every field value (a text, each
/// item of a list, a reference's id and URL), and every recorded answer sits inside an inline fence it
/// cannot close (<see cref="Fences.Inline"/>); the project, type, flow, stage, step, and field names,
/// the labels, and the assembler's own words come from registration and definitions, not from a
/// parcel's text, and stay outside every fence, so an unset field and a text reading "not set" differ.
/// </summary>
public static class ContextAssembler
{
    /// <summary>The word every field entry's first line begins with.</summary>
    public const string FieldLabel = "Field";

    /// <summary>The word every source line begins with.</summary>
    public const string SourceLabel = "Source";

    public const string NotSet = "not set";

    /// <summary>What a source line says of a step with no recorded visit on the flow run.</summary>
    public const string NotRun = "has not run on this flow run yet";

    /// <summary>The most open parcels a type source lists; the most recently created ones are kept.</summary>
    public const int OpenParcelLimit = 50;

    /// <summary>
    /// Writes a recorded answer or result as compact JSON whose text reads as written: characters such
    /// as <c>&lt;</c>, <c>&amp;</c>, an apostrophe, and non-ASCII letters are not escaped. The context
    /// is never embedded in HTML, which is what the relaxed encoder's caution concerns, and the
    /// flow's canonical JSON and hash never use these options.
    /// </summary>
    private static readonly JsonSerializerOptions SourceJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <param name="parcel">The parcel the step's flow run is on.</param>
    /// <param name="step">The step about to spawn.</param>
    /// <param name="visits">Every visit of the flow run the step runs on, in the order entered (<see cref="FlowRun.Steps"/>).</param>
    /// <param name="reads">What the step's sources of a named flow, a type, or the origin read from this node's record.</param>
    public static string Assemble(Parcel parcel, FlowStep step, IReadOnlyList<FlowRunStepEntry> visits, SourceReads reads)
    {
        ArgumentNullException.ThrowIfNull(parcel);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(visits);
        ArgumentNullException.ThrowIfNull(reads);

        string[] fixedLines =
        [
            $"Parcel: {parcel.Id}",
            $"Project: {Fences.OneLine(parcel.Project)}",
            $"Type: {Fences.OneLine(parcel.TypeName)}",
            $"Stage: {Fences.OneLine(parcel.Stage)}",
            $"Title: {Fences.Inline(parcel.Title)}",
            $"Step: {Fences.OneLine(step.Name)}",
        ];

        return string.Join('\n',
        [
            Fences.Framing,
            string.Empty,
            .. fixedLines,
            .. step.Fields.SelectMany(field => FieldLines(field, parcel.ValueOf(field))),
            .. step.Sources.SelectMany(source => source switch
            {
                { Flow: null, Type: null, Origin: null, Step: { } own } => [SourceLine(own, visits)],
                { Flow: { } flow } => [NamedFlowLine(flow, source.Step ?? string.Empty, reads.FlowRuns)],
                { Type: { } type } => TypeLines(type, parcel.Id, reads),
                _ => OriginLines(parcel, reads),
            }),
        ]);
    }

    /// <summary>
    /// The parcel's derived-from relation an origin source reads: the first one its stream records, or
    /// null when it records none. It is not <see cref="Parcel.Origin"/>, which keys a parcel to the cause
    /// that created it.
    /// </summary>
    public static ParcelRelation? OriginOf(Parcel parcel)
    {
        ArgumentNullException.ThrowIfNull(parcel);
        return parcel.Relations.FirstOrDefault(relation => string.Equals(relation.Kind, RelationKinds.DerivedFrom, StringComparison.Ordinal));
    }

    /// <summary>
    /// A source's line: the step's latest visit on the flow run that recorded an answer or result, with
    /// that visit's number among the step's visits counting from 1, when it recorded, and what it
    /// recorded; or, when no visit of the step has recorded, that it has not run on this flow run yet.
    /// </summary>
    private static string SourceLine(string source, IReadOnlyList<FlowRunStepEntry> visits)
    {
        string label = $"{SourceLabel} {Fences.OneLine(source)}:";
        FlowRunStepEntry[] ofSource = [.. visits.Where(entry => string.Equals(entry.Step, source, StringComparison.Ordinal))];
        int latest = Array.FindLastIndex(ofSource, entry => entry.IsRecorded && (entry.Answer ?? entry.Result) is not null);

        return latest >= 0 && ofSource[latest] is { RecordedAt: { } recordedAt } visit && (visit.Answer ?? visit.Result) is JsonElement recorded
            ? $"{label} visit {latest + 1}, recorded {recordedAt.ToString("O", CultureInfo.InvariantCulture)}: {Fences.Inline(JsonSerializer.Serialize(recorded, SourceJson))}"
            : $"{label} {NotRun}";
    }

    /// <summary>
    /// A named flow's source line: the latest recording of <paramref name="step"/> across every flow run
    /// of <paramref name="flow"/> on the parcel, with the flow run's id, the visit's number among that
    /// step's visits in that flow run, when it was recorded, and what it recorded; or, when none
    /// recorded it, that no flow run of that flow on this parcel has. The latest is the one with the
    /// latest recorded time; a tie goes to the later flow run in <paramref name="flowRuns"/>'s order,
    /// and within one flow run to the later visit.
    /// </summary>
    private static string NamedFlowLine(string flow, string step, IReadOnlyList<FlowRun> flowRuns)
    {
        string label = $"{SourceLabel} flow {Fences.OneLine(flow)} step {Fences.OneLine(step)}:";
        (FlowRun Run, Recording Recording)? latest = null;
        foreach (FlowRun run in flowRuns.Where(run => DefinitionResolution.SameName(run.FlowName, flow)))
        {
            foreach (Recording recording in RecordingsOf(run, step))
            {
                if (latest is not { } kept || recording.At >= kept.Recording.At)
                {
                    latest = (run, recording);
                }
            }
        }

        return latest is { } found
            ? $"{label} flow run {found.Run.Id}, {Recorded(found.Recording)}"
            : $"{label} no flow run of flow {Fences.OneLine(flow)} on this parcel has recorded step {Fences.OneLine(step)}";
    }

    /// <summary>A visit that recorded an answer or result, with its number among its step's visits in its flow run counting from 1.</summary>
    private sealed record Recording(int Visit, DateTimeOffset At, JsonElement Value);

    /// <summary>Every recorded visit of <paramref name="step"/> in <paramref name="run"/>, in the order entered.</summary>
    private static IEnumerable<Recording> RecordingsOf(FlowRun run, string step) =>
        run.Steps
            .Where(entry => string.Equals(entry.Step, step, StringComparison.Ordinal))
            .Select((entry, index) => entry is { RecordedAt: { } at } && (entry.Answer ?? entry.Result) is JsonElement value
                ? new Recording(index + 1, at, value)
                : null)
            .OfType<Recording>();

    /// <summary>The latest of <paramref name="step"/>'s recordings in <paramref name="run"/>, a tie going to the later visit, or null.</summary>
    private static Recording? LatestOf(FlowRun run, string step) =>
        RecordingsOf(run, step).Aggregate((Recording?)null, (kept, recording) => kept is null || recording.At >= kept.At ? recording : kept);

    private static string Recorded(Recording recording) =>
        $"visit {recording.Visit}, recorded {recording.At.ToString("O", CultureInfo.InvariantCulture)}: {Fences.Inline(JsonSerializer.Serialize(recording.Value, SourceJson))}";

    /// <summary>
    /// A type source's lines. A type that does not resolve for the project gets one line saying it is
    /// not present, whatever parcels of that name the record holds. Otherwise the open parcels of the
    /// type other than <paramref name="own"/>, at most <see cref="OpenParcelLimit"/> of the most recently
    /// created, in creation order with a tie ordered by id, each with its fields, and a line saying how
    /// many were left out when more are open; or one line saying no other open parcel of it is held.
    /// </summary>
    private static IEnumerable<string> TypeLines(string type, Guid own, SourceReads reads)
    {
        string name = Fences.OneLine(type);
        string label = $"{SourceLabel} open parcels of type {name}:";
        if (!reads.ResolvedTypes.Contains(type))
        {
            return [$"{label} type {name} is not present in this project"];
        }

        Parcel[] open =
        [
            .. reads.Parcels
                .Where(parcel => !parcel.IsClosed && parcel.Id != own && DefinitionResolution.SameName(parcel.TypeName, type))
                .OrderBy(parcel => parcel.CreatedAt)
                .ThenBy(parcel => parcel.Id),
        ];
        if (open.Length == 0)
        {
            return [$"{label} no other open parcel of type {name} is in this node's record"];
        }

        int left = Math.Max(0, open.Length - OpenParcelLimit);
        return
        [
            label,
            .. left > 0 ? [$"- {left} earlier created open parcels of type {name} were left out"] : Array.Empty<string>(),
            .. open.Skip(left).SelectMany(parcel => (string[])
            [
                $"- parcel {parcel.Id}, stage {Fences.OneLine(parcel.Stage)}, title {Fences.Inline(parcel.Title)}",
                .. HeldFieldLines(parcel, "  "),
            ]),
        ];
    }

    /// <summary>
    /// An origin source's lines, from the parcel's derived-from relation (<see cref="OriginOf"/>): one
    /// line when it records none; otherwise the parcel it was derived from and then the flow run the
    /// relation names, each under its own source line and each rendered whether or not the other is.
    /// </summary>
    private static IEnumerable<string> OriginLines(Parcel parcel, SourceReads reads)
    {
        if (OriginOf(parcel) is not { } relation)
        {
            return [$"{SourceLabel} origin: this parcel records no origin"];
        }

        string parcelLabel = $"{SourceLabel} origin parcel:";
        string[] fromParcel = reads.OriginParcel is { } from && from.Id == relation.RelatedParcelId
            ?
            [
                $"{parcelLabel} parcel {from.Id}, type {Fences.OneLine(from.TypeName)}, stage {Fences.OneLine(from.Stage)}, title {Fences.Inline(from.Title)}",
                .. HeldFieldLines(from, string.Empty),
            ]
            : [$"{parcelLabel} parcel {relation.RelatedParcelId} is not in this node's record"];

        string runLabel = $"{SourceLabel} origin flow run:";
        string[] fromRun = (relation.FlowRunId, reads.OriginFlowRun) switch
        {
            (null, _) => [$"{runLabel} the derived-from relation names no flow run"],
            ({ } id, { } run) when run.Id == id => [$"{runLabel} flow run {run.Id} of flow {Fences.OneLine(run.FlowName)}", .. RunLines(run)],
            ({ } id, _) => [$"{runLabel} flow run {id} is not in this node's record"],
        };

        return [.. fromParcel, .. fromRun];
    }

    /// <summary>For each step <paramref name="run"/> recorded, in the order its steps were first entered, its latest recording.</summary>
    private static IEnumerable<string> RunLines(FlowRun run)
    {
        string[] lines =
        [
            .. run.Steps
                .Select(entry => entry.Step)
                .Distinct(StringComparer.Ordinal)
                .Select(step => LatestOf(run, step) is { } latest
                    ? $"- step {Fences.OneLine(step)}: {Recorded(latest)}"
                    : null)
                .OfType<string>(),
        ];
        return lines.Length > 0 ? lines : ["- no step of this flow run has recorded"];
    }

    /// <summary>Every field <paramref name="parcel"/> holds, in ordinal order of field name, each under a hyphen after <paramref name="indent"/>.</summary>
    private static IEnumerable<string> HeldFieldLines(Parcel parcel, string indent) =>
        parcel.Fields
            .OrderBy(field => field.Key, StringComparer.Ordinal)
            .SelectMany(field => ValueLines($"{indent}- field {Fences.OneLine(field.Key)}:", field.Value, $"{indent}  - "));

    /// <summary>
    /// A field's entry: its label and its value on one line, or for a list, its label alone and then
    /// each item on a line of its own beginning with a hyphen.
    /// </summary>
    private static IEnumerable<string> FieldLines(string field, FieldValue? value) =>
        ValueLines($"{FieldLabel} {Fences.OneLine(field)}:", value, "- ");

    /// <summary>A value after its label on one line, or for a list, the label alone and then each item on a line of its own after <paramref name="itemPrefix"/>.</summary>
    private static IEnumerable<string> ValueLines(string label, FieldValue? value, string itemPrefix) =>
        value switch
        {
            { Text: { } text } => [$"{label} {Fences.Inline(text)}"],
            { Items: { } items } => [label, .. items.Select(item => $"{itemPrefix}{Fences.Inline(item)}")],
            { Reference: { Url: { } url } reference } => [$"{label} {Fences.Inline(reference.Id)} ({Fences.Inline(url)})"],
            { Reference: { } reference } => [$"{label} {Fences.Inline(reference.Id)}"],
            _ => [$"{label} {NotSet}"],
        };
}
