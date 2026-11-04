using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Coanda.Core.Contours;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;

namespace Coanda.Daemon.Executors;

/// <summary>
/// Builds the per-parcel context body a step's spawn carries. It reads only the parcel record, the
/// step, and the visits of the flow the step runs on; it never takes the agent definition, so the
/// persona reaches the executor only through the definition on the spawn and is never folded into
/// the context. Six fixed lines come first;
/// after them, one entry per field the step declares, in the step's order, holding the field's
/// value or "not set"; after those, one line per source the step declares, in the step's order,
/// holding the latest recorded visit of the source step on this flow since the step's own previous
/// visit, or saying the source has recorded nothing since then, or has not run on this flow yet. A
/// recording made before the step's previous visit is never carried again, so a step acting on its
/// sources is not told to act on what it has already dealt with. Each value is folded onto its own line, so a title, a field value, or a recorded
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

    /// <summary>What a source line says of a step with no recorded visit on the flow.</summary>
    public const string NotRun = "has not run on this flow yet";

    /// <summary>
    /// What a source line says of a step whose every recorded visit came before the declaring step's
    /// own latest recorded visit, ahead of that visit's number.
    /// </summary>
    public const string NothingSince = "has recorded nothing since";

    /// <summary>
    /// Writes a recorded answer or result as compact JSON whose text reads as written: characters such
    /// as <c>&lt;</c>, <c>&amp;</c>, an apostrophe, and non-ASCII letters are not escaped. The context
    /// is never embedded in HTML, which is what the relaxed encoder's caution concerns, and the
    /// contour's canonical JSON and hash never use these options.
    /// </summary>
    private static readonly JsonSerializerOptions SourceJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <param name="parcel">The parcel the step's flow runs on.</param>
    /// <param name="step">The step about to spawn.</param>
    /// <param name="visits">Every visit of the flow the step runs on, in the order entered (<see cref="Flow.Steps"/>).</param>
    public static string Assemble(Parcel parcel, ContourStep step, IReadOnlyList<FlowStepEntry> visits)
    {
        ArgumentNullException.ThrowIfNull(parcel);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(visits);

        string[] fixedLines =
        [
            $"Parcel: {parcel.Id}",
            $"Project: {OneLine(parcel.Project)}",
            $"Type: {OneLine(parcel.TypeName)}",
            $"State: {OneLine(parcel.State)}",
            $"Title: {OneLine(parcel.Title)}",
            $"Step: {OneLine(step.Name)}",
        ];

        return string.Join('\n',
        [
            .. fixedLines,
            .. step.Fields.SelectMany(field => FieldLines(field, parcel.ValueOf(field))),
            .. step.Sources.Select(source => SourceLine(source.Step ?? string.Empty, step.Name, visits)),
        ]);
    }

    /// <summary>
    /// A source's line: the source step's latest visit on the flow that recorded an answer or result
    /// after the declaring step's previous visit (see <see cref="PreviousVisit"/>), with that visit's
    /// number among the source step's visits counting from 1, when it recorded, and what it recorded.
    /// When the source step recorded only before that previous visit, the line says it has recorded
    /// nothing since, naming that visit's number; when it never recorded, that it has not run on this
    /// flow yet. Visits are compared by their position in the flow, which is the order entered.
    /// </summary>
    private static string SourceLine(string source, string declaring, IReadOnlyList<FlowStepEntry> visits)
    {
        string label = $"{SourceLabel} {OneLine(source)}:";
        int since = PreviousVisit(visits, declaring);
        int latest = LastIndex(visits, visits.Count - 1, entry => Named(entry, source) && entry.IsRecorded && (entry.Answer ?? entry.Result) is not null);

        return latest switch
        {
            < 0 => $"{label} {NotRun}",
            _ when latest < since =>
                $"{label} {NothingSince} {OneLine(declaring)} visit {VisitNumber(visits, since)}",
            _ when visits[latest] is { RecordedAt: { } recordedAt } visit && (visit.Answer ?? visit.Result) is JsonElement recorded =>
                $"{label} visit {VisitNumber(visits, latest)}, recorded {recordedAt.ToString("O", CultureInfo.InvariantCulture)}: {OneLine(JsonSerializer.Serialize(recorded, SourceJson))}",
            _ => $"{label} {NotRun}",
        };
    }

    /// <summary>
    /// The position of the declaring step's previous visit, the one whose sources it has already acted
    /// on, or -1 when it has none: its latest recorded visit before the run of its own visits the flow
    /// ends on. That run holds the visit about to spawn and any visit a resume performed again in its
    /// place (a blocked answer, say), which never acted on the sources that routed the step there.
    /// </summary>
    private static int PreviousVisit(IReadOnlyList<FlowStepEntry> visits, string declaring)
    {
        int beforeRun = LastIndex(visits, visits.Count - 1, entry => !Named(entry, declaring));
        return LastIndex(visits, beforeRun, entry => Named(entry, declaring) && entry.IsRecorded);
    }

    private static bool Named(FlowStepEntry entry, string step) => string.Equals(entry.Step, step, StringComparison.Ordinal);

    /// <summary>The number, counting from 1, of the visit at <paramref name="index"/> among the visits of its own step.</summary>
    private static int VisitNumber(IReadOnlyList<FlowStepEntry> visits, int index) =>
        visits.Take(index + 1).Count(entry => Named(entry, visits[index].Step));

    /// <summary>The position of the last visit at or before <paramref name="from"/> that matches, or -1.</summary>
    private static int LastIndex(IReadOnlyList<FlowStepEntry> visits, int from, Func<FlowStepEntry, bool> match)
    {
        for (int index = from; index >= 0; index--)
        {
            if (match(visits[index]))
            {
                return index;
            }
        }

        return -1;
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
