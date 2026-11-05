using System.Text.Json.Serialization;

namespace Coanda.Core.Types;

/// <summary>
/// A named field a parcel type declares: what <see cref="Kind"/> of value it holds, and the stages a
/// parcel must hold a value in it to leave, which may be none. A type that declares a field required
/// to leave a stage binds the required-fields gate to every move its table holds out of that stage;
/// validation refuses one that leaves such a move unbound. A field may name the module that owns it,
/// which alone, beside the owner, sets it.
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

    public bool IsRequiredToLeave(string stage) => RequiredToLeave.Contains(stage, StringComparer.Ordinal);
}
