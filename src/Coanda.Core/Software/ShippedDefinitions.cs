using Coanda.Core.Definitions;

namespace Coanda.Core.Software;

/// <summary>The definitions the binary carries, ready to seed: the software lifecycle's types, flows, and agents.</summary>
public static class ShippedDefinitions
{
    /// <summary>The parcel types the binary carries, as definitions.</summary>
    public static IReadOnlyList<ShippedDefinition> ParcelTypes { get; } = [.. ShippedTypes.All.Select(ShippedDefinition.FromParcelType)];

    /// <summary>The flows the binary carries, as definitions.</summary>
    public static IReadOnlyList<ShippedDefinition> Flows { get; } = [.. ShippedFlows.All.Select(ShippedDefinition.FromFlow)];

    /// <summary>The agents the binary carries, as definitions.</summary>
    public static IReadOnlyList<ShippedDefinition> Agents { get; } = [.. ShippedAgents.All.Select(ShippedDefinition.FromAgent)];

    /// <summary>Every definition the binary carries, in the order the daemon seeds it.</summary>
    public static IReadOnlyList<ShippedDefinition> All { get; } = [.. ParcelTypes, .. Flows, .. Agents];
}
