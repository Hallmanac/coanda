using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Coanda.Core.Flows;
using Coanda.Core.FlowRuns;
using Coanda.Core.Parcels;

namespace Coanda.Daemon.Executors;

/// <summary>
/// Builds the per-parcel context body a step's spawn carries. It reads only the parcel record, the
/// step, and the visits of the flow run the step runs on; it never takes the agent definition, so the
/// persona reaches the executor only through the definition on the spawn and is never folded into
/// the context. Six fixed lines come first;
/// after them, one entry per field the step declares, in the step's order, holding the field's
/// value or "not set"; after those, one line per source the step declares, in the step's order,
/// holding the latest recorded visit of the source step on this flow run or saying it has not run on
/// this flow run yet. Each value is folded onto its own line, so a title, a field value, or a recorded
/// answer holding a line break cannot start a labelled line of its own, and every field entry begins
/// with <see cref="FieldLabel"/> and every source line with <see cref="SourceLabel"/>, so none reads
/// as one of the fixed labels.
/// </summary>
public static partial class ContextAssembler
{
    /// <summary>The word every field entry's first line begins with.</summary>
    public const string FieldLabel = "Field";

    /// <summary>The word every source line begins with.</summary>
    public const string SourceLabel = "Source";

    public const string NotSet = "not set";

    /// <summary>What a source line says of a step with no recorded visit on the flow run.</summary>
    public const string NotRun = "has not run on this flow run yet";

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
            $"Project: {OneLine(parcel.Project)}",
            $"Type: {OneLine(parcel.TypeName)}",
            $"Stage: {OneLine(parcel.Stage)}",
            $"Title: {OneLine(parcel.Title)}",
            $"Step: {OneLine(step.Name)}",
        ];

        return string.Join('\n',
        [
            .. fixedLines,
            .. step.Fields.SelectMany(field => FieldLines(field, parcel.ValueOf(field))),
            .. step.Sources.Select(source => SourceLine(source.Step ?? string.Empty, visits)),
        ]);
    }

    /// <summary>
    /// A source's line: the step's latest visit on the flow run that recorded an answer or result, with
    /// that visit's number among the step's visits counting from 1, when it recorded, and what it
    /// recorded; or, when no visit of the step has recorded, that it has not run on this flow run yet.
    /// </summary>
    private static string SourceLine(string source, IReadOnlyList<FlowRunStepEntry> visits)
    {
        string label = $"{SourceLabel} {OneLine(source)}:";
        FlowRunStepEntry[] ofSource = [.. visits.Where(entry => string.Equals(entry.Step, source, StringComparison.Ordinal))];
        int latest = Array.FindLastIndex(ofSource, entry => entry.IsRecorded && (entry.Answer ?? entry.Result) is not null);

        return latest >= 0 && ofSource[latest] is { RecordedAt: { } recordedAt } visit && (visit.Answer ?? visit.Result) is JsonElement recorded
            ? $"{label} visit {latest + 1}, recorded {recordedAt.ToString("O", CultureInfo.InvariantCulture)}: {OneLine(JsonSerializer.Serialize(recorded, SourceJson))}"
            : $"{label} {NotRun}";
    }

    /// <summary>
    /// A field's entry: its label and its value on one line, or for a list, its label alone and then
    /// each item on a line of its own beginning with a hyphen.
    /// </summary>
    private static IEnumerable<string> FieldLines(string field, FieldValue? value)
    {
        string label = $"{FieldLabel} {OneLine(field)}:";
        return value switch
        {
            { Text: { } text } => [$"{label} {OneLine(text)}"],
            { Items: { } items } => [label, .. items.Select(item => $"- {OneLine(item)}")],
            { Reference: { Url: { } url } reference } => [$"{label} {OneLine(reference.Id)} ({OneLine(url)})"],
            { Reference: { } reference } => [$"{label} {OneLine(reference.Id)}"],
            _ => [$"{label} {NotSet}"],
        };
    }

    /// <summary>Replaces each run of control characters or line separators with one space.</summary>
    private static string OneLine(string value) => LineBreaking().Replace(value, " ");

    [GeneratedRegex(@"[\p{Cc}\u2028\u2029]+")]
    private static partial Regex LineBreaking();
}
