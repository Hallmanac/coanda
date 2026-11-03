using Coanda.Core.Types;

namespace Coanda.Core.Definitions;

/// <summary>
/// One definition the binary carries, ready to seed into the store. Seeding is generic over
/// <see cref="DefinitionKind"/>: the flow and agent definitions arrive through the same shape.
/// </summary>
public sealed record ShippedDefinition(DefinitionKind Kind, string Name, string Content, string ContentHash)
{
    public static ShippedDefinition FromParcelType(ParcelType type) =>
        new(DefinitionKind.ParcelType, type.Name, ParcelType.CanonicalJson(type), type.Hash);

    /// <summary>The parcel types the binary carries, as definitions.</summary>
    public static IReadOnlyList<ShippedDefinition> ShippedParcelTypes { get; } = [.. ShippedTypes.All.Select(FromParcelType)];
}
