using Coanda.Core.Types;

namespace Coanda.Tests.Core;

/// <summary>Shipped parcel types as an earlier build carried them, for tests of what a store seeded by that build holds.</summary>
public static class PreviousTypes
{
    /// <summary>The idea type as main shipped it before it declared the task type as the one type it may derive: today's, with no derive list.</summary>
    public static ParcelType IdeaBeforeDerive => SoftwareTypes.Idea with { Derives = [] };
}
