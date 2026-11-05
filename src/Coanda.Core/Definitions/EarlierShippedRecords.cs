namespace Coanda.Core.Definitions;

/// <summary>
/// The module a definition recorded in shipped scope, before module scope took its place, is
/// attributed to. Every such record was one of the software engineering definitions the binary
/// carried, so it reads as the software module's, at the version whose manifest first carried those
/// definitions as files: the eleven that version imports have the content hashes the binary gave them,
/// so a store that already holds them imports nothing new, and a fresh store and an upgraded one read
/// the same module and version for each of them.
/// </summary>
public static class EarlierShippedRecords
{
    public const string Module = "software";

    public const string Version = "0.1.0";
}
