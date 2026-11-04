using Coanda.Core.Parcels;
using Coanda.Daemon.Api.Endpoints;
using Spectre.Console;

namespace Coanda.Cli.Commands;

/// <summary>
/// The field rows <c>coa parcel show</c> writes: every field the parcel's type version declares, in
/// its order, with the value the parcel holds or the words "not set". Every cell is escaped markup.
/// </summary>
internal static class ParcelRendering
{
    public const string NotSet = "not set";

    public static IEnumerable<string[]> FieldRows(ParcelView parcel) =>
        parcel.Fields.Select(field => new[] { Markup.Escape(field.Name), Markup.Escape(Describe(field.Value)) });

    private static string Describe(FieldValue? value) => value switch
    {
        { Text: { } text } => text,
        { Items: { } items } => string.Join('\n', items.Select(item => $"- {item}")),
        { Reference: { Url: { } url } reference } => $"{reference.Id} ({url})",
        { Reference: { } reference } => reference.Id,
        _ => NotSet,
    };
}
