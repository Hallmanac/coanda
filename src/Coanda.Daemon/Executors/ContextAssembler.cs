using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Coanda.Core.Flows;
using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;

namespace Coanda.Daemon.Executors;

/// <summary>
/// Builds the per-parcel context body a step's spawn carries. It reads only the parcel record, the
/// step, and the visits of the flow run the step runs on; it never takes the agent definition, so the
/// persona reaches the executor only through the definition on the spawn and is never folded into
/// the context. It opens with <see cref="Fences.Framing"/> and a blank line; six fixed lines come next;
/// after them, one entry per field the step declares, in the step's order, holding the field's
/// value or "not set"; after those, one line per source the step declares, in the step's order,
/// holding the latest recorded visit of the source step on this flow run or saying it has not run on
/// this flow run yet, or, for a source of another kind, naming it and saying this context does not
/// carry it yet. Each value is folded onto its own line, so a title, a field value, or a recorded
/// answer holding a line break cannot start a labelled line of its own, and every field entry begins
/// with <see cref="FieldLabel"/> and every source line with <see cref="SourceLabel"/>, so none reads
/// as one of the fixed labels. The title, every field value (a text, each item of a list, a
/// reference's id and URL), and every recorded answer sits inside an inline fence it cannot close
/// (<see cref="Fences.Inline"/>); the project, type, stage, and step, the labels, and the words
/// <see cref="NotSet"/> and <see cref="NotRun"/> come from registration and definitions, not from a
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

    /// <summary>What a source line says of a source whose kind this context does not render yet.</summary>
    public const string NotCarried = "this context does not carry it yet";

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
    public static string Assemble(Parcel parcel, FlowStep step, IReadOnlyList<FlowRunStepEntry> visits)
    {
        ArgumentNullException.ThrowIfNull(parcel);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(visits);

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
            .. step.Sources.Select(source => source is { Flow: null, Type: null, Origin: null, Step: { } own }
                ? SourceLine(own, visits)
                : NotCarriedLine(source)),
        ]);
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
    /// The line for a source of a kind this context does not render yet (a named flow's step, the open
    /// parcels of a type, or the origin): it names the source and says it is not carried. It never
    /// reads the flow run's visits, so a named flow's source naming this flow and one of its steps is
    /// not rendered as that step's visit.
    /// </summary>
    private static string NotCarriedLine(FlowSource source)
    {
        string named = source switch
        {
            { Type: { } type } => $"open parcels of type {Fences.OneLine(type)}",
            { Flow: { } flow } => $"flow {Fences.OneLine(flow)} step {Fences.OneLine(source.Step ?? string.Empty)}",
            _ => "origin",
        };

        return $"{SourceLabel} {named}: {NotCarried}";
    }

    /// <summary>
    /// A field's entry: its label and its value on one line, or for a list, its label alone and then
    /// each item on a line of its own beginning with a hyphen.
    /// </summary>
    private static IEnumerable<string> FieldLines(string field, FieldValue? value)
    {
        string label = $"{FieldLabel} {Fences.OneLine(field)}:";
        return value switch
        {
            { Text: { } text } => [$"{label} {Fences.Inline(text)}"],
            { Items: { } items } => [label, .. items.Select(item => $"- {Fences.Inline(item)}")],
            { Reference: { Url: { } url } reference } => [$"{label} {Fences.Inline(reference.Id)} ({Fences.Inline(url)})"],
            { Reference: { } reference } => [$"{label} {Fences.Inline(reference.Id)}"],
            _ => [$"{label} {NotSet}"],
        };
    }
}
