using System.Diagnostics.CodeAnalysis;

namespace Coanda.Core.Parcels;

/// <summary>
/// The kinds of relation a parcel records to another parcel of its project. A kind is stored as its
/// text, so a later kind is a new value of the same shape rather than a new shape.
/// </summary>
public static class RelationKinds
{
    /// <summary>The parcel was derived from the other parcel; recorded only in the save that creates it.</summary>
    public const string DerivedFrom = "derived-from";

    /// <summary>The other parcel blocks the parcel.</summary>
    public const string BlockedBy = "blocked-by";

    public static IReadOnlyList<string> All { get; } = [DerivedFrom, BlockedBy];

    public static bool IsKnown([NotNullWhen(true)] string? kind) => kind is not null && All.Contains(kind, StringComparer.Ordinal);
}
