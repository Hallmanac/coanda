namespace Coanda.Sdk.Contract;

/// <summary>
/// Where a module asks its feed to start on a node where its position has never been written. A node
/// that already holds the module's position reads past that position whichever the module asks.
/// </summary>
public enum FeedStart
{
    /// <summary>At the end of the record as it stands when the feed first runs: nothing recorded before is delivered.</summary>
    Head,

    /// <summary>At the beginning of the record: every event recorded before the feed first ran is delivered first.</summary>
    Beginning,
}
