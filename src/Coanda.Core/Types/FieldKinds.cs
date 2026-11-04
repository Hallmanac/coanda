namespace Coanda.Core.Types;

/// <summary>The kinds of value a field holds: a text, a list of texts, or a reference (an identifier and an optional URL).</summary>
public static class FieldKinds
{
    public const string Text = "text";
    public const string TextList = "text-list";
    public const string Reference = "reference";

    public static readonly IReadOnlyList<string> All = [Text, TextList, Reference];

    public static bool IsKnown(string? kind) => All.Contains(kind, StringComparer.Ordinal);
}
