namespace Coanda.Core.Types;

/// <summary>
/// A named field a parcel type declares: what <see cref="Kind"/> of value it holds, and the stages a
/// parcel must hold a value in it to leave, which may be none. A type that declares a field required
/// to leave a stage binds the required-fields gate to every move its table holds out of that stage;
/// validation refuses one that leaves such a move unbound.
/// </summary>
public sealed record FieldDeclaration
{
    public required string Name { get; init; }

    /// <summary>One of <see cref="FieldKinds.All"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>The live stages a parcel must hold a value in this field to leave.</summary>
    public IReadOnlyList<string> RequiredToLeave { get; init; } = [];

    public bool IsRequiredToLeave(string stage) => RequiredToLeave.Contains(stage, StringComparer.Ordinal);
}
