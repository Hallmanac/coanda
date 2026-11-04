namespace Coanda.Core.Types;

/// <summary>
/// A named field a parcel type declares: what <see cref="Kind"/> of value it holds, and the states a
/// parcel must hold a value in it to leave, which may be none. Whether a move out of one of those
/// states is actually held back is the type's own choice, made by binding the required-fields gate
/// to the move.
/// </summary>
public sealed record FieldDeclaration
{
    public required string Name { get; init; }

    /// <summary>One of <see cref="FieldKinds.All"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>The live states a parcel must hold a value in this field to leave.</summary>
    public IReadOnlyList<string> RequiredToLeave { get; init; } = [];

    public bool IsRequiredToLeave(string state) => RequiredToLeave.Contains(state, StringComparer.Ordinal);
}
