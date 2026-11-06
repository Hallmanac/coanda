namespace Coanda.Core.Parcels;

/// <summary>Where a parcel's claims leave this node (<see cref="ClaimDecider.Standing"/>).</summary>
public abstract record ClaimStanding
{
    private ClaimStanding()
    {
    }

    /// <summary>No claim on the parcel is stored.</summary>
    public sealed record Unclaimed : ClaimStanding;

    /// <summary>This node holds the parcel: its claim wins, and it was pushed and its peers fetched since.</summary>
    public sealed record Held : ClaimStanding;

    /// <summary>This node's claim may win, but this node does not hold the parcel yet, for <paramref name="Reason"/>.</summary>
    public sealed record NotYetHeld(string Reason) : ClaimStanding;

    /// <summary>
    /// Another node's claim wins: <paramref name="Winner"/>'s. Either this node's own claim lost to it, or this
    /// node made none.
    /// </summary>
    public sealed record Lost(Guid Winner) : ClaimStanding;
}
