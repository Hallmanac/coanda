using System.Text.RegularExpressions;
using Coanda.Core.Contours;
using Coanda.Core.Parcels;

namespace Coanda.Daemon.Executors;

/// <summary>
/// Builds the per-parcel context body a step's spawn carries. It reads only the parcel record and
/// the step; it never takes the agent definition, so the persona reaches the executor only through
/// the definition on the spawn and is never folded into the context. Six fixed lines come first;
/// after them, one entry per field the step declares, in the step's order, holding the field's
/// value or "not set". Each value is folded onto its own line, so a title or a field value holding a
/// line break cannot start a labelled line of its own, and every field entry begins with
/// <see cref="FieldLabel"/>, so none reads as one of the fixed labels.
/// </summary>
public static partial class ContextAssembler
{
    /// <summary>The word every field entry's first line begins with.</summary>
    public const string FieldLabel = "Field";

    public const string NotSet = "not set";

    public static string Assemble(Parcel parcel, ContourStep step)
    {
        ArgumentNullException.ThrowIfNull(parcel);
        ArgumentNullException.ThrowIfNull(step);

        string[] fixedLines =
        [
            $"Parcel: {parcel.Id}",
            $"Project: {OneLine(parcel.Project)}",
            $"Type: {OneLine(parcel.TypeName)}",
            $"State: {OneLine(parcel.State)}",
            $"Title: {OneLine(parcel.Title)}",
            $"Step: {OneLine(step.Name)}",
        ];

        return string.Join('\n', [.. fixedLines, .. step.Fields.SelectMany(field => FieldLines(field, parcel.ValueOf(field)))]);
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
