using System.Text.Json.Serialization;

namespace Coanda.Core.Types;

/// <summary>
/// A named field a parcel type declares: what <see cref="Kind"/> of value it holds, and the stages a
/// parcel must hold a value in it to leave, which may be none. A type that declares a field required
/// to leave a stage binds the required-fields gate to every move its table holds out of that stage;
/// validation refuses one that leaves such a move unbound. A field may name the module that owns it,
/// which alone, beside the owner, sets it, and may be marked as its type's tag field.
/// </summary>
public sealed record FieldDeclaration
{
    public required string Name { get; init; }

    /// <summary>One of <see cref="FieldKinds.All"/>.</summary>
    public required string Kind { get; init; }

    /// <summary>The live stages a parcel must hold a value in this field to leave.</summary>
    public IReadOnlyList<string> RequiredToLeave { get; init; } = [];

    /// <summary>
    /// The module that owns the field, by its name (<see cref="Registry.ModuleNames.IsModuleName"/>), or
    /// null when no module does. A field a module owns is set by that module, on any parcel of a project
    /// it is switched on for, and by an Owner-role principal, and by nobody else. It is written into the
    /// type's content only when it names a module, so a type whose fields name none reads and hashes as
    /// it did before owners existed.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Owner { get; init; }

    /// <summary>
    /// Whether this is its type's tag field: the module-owned text-list whose values name the repos that
    /// carry the events of a parcel of the type, read when the node exports them (design section 6). A
    /// type marks at most one. It is written into the type's content only when set, so a type that
    /// marks none reads and hashes as it did before the mark existed.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Tag { get; init; }

    public bool IsRequiredToLeave(string stage) => RequiredToLeave.Contains(stage, StringComparer.Ordinal);
}
