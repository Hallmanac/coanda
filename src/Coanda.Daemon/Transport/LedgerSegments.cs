using System.Text;

namespace Coanda.Daemon.Transport;

/// <summary>
/// An origin's ledger in one project as the files of its ref: one file per segment, named
/// <c>yyyy-MM.jsonl</c>, holding the segment's lines in sequence order, each ended by a line feed. They are
/// made from the store's records alone, so the same records always give the same bytes. They are either the
/// whole chain's files or the files of its later segments alone, from the first line of a segment on.
/// </summary>
public sealed class LedgerSegments
{
    public const string Extension = ".jsonl";

    private LedgerSegments(IReadOnlyDictionary<string, byte[]> files, string lastHash, long lastSegmentStart, bool whole)
    {
        Files = files;
        LastHash = lastHash;
        LastSegmentStart = lastSegmentStart;
        Whole = whole;
    }

    /// <summary>Each file's bytes by its name, in name order.</summary>
    public IReadOnlyDictionary<string, byte[]> Files { get; }

    /// <summary>The hash of the chain's last line, which names this ledger as a whole.</summary>
    public string LastHash { get; }

    /// <summary>
    /// The sequence of the first line of the last segment: a later line is written in that segment or one
    /// after it, so the files of the lines from this one on are every file a longer chain changes.
    /// </summary>
    public long LastSegmentStart { get; }

    /// <summary>Whether these are the files of the whole chain, from its first line, rather than of its later segments alone.</summary>
    public bool Whole { get; }

    /// <summary>
    /// The files of <paramref name="lines"/>, which are one origin's, in sequence order, and not empty: the
    /// whole chain, or every line from the first line of a segment on.
    /// </summary>
    public static LedgerSegments Of(IReadOnlyList<LedgerLine> lines)
    {
        ArgumentOutOfRangeException.ThrowIfZero(lines.Count, nameof(lines));
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (IGrouping<string, LedgerLine> segment in lines.GroupBy(l => l.Segment))
        {
            var text = new StringBuilder();
            foreach (LedgerLine line in segment)
            {
                text.Append(line.Line).Append('\n');
            }

            files.Add(segment.Key + Extension, Encoding.UTF8.GetBytes(text.ToString()));
        }

        string lastSegment = lines[^1].Segment;
        long lastSegmentStart = lines.First(l => l.Segment == lastSegment).Sequence;
        return new LedgerSegments(files, lines[^1].Hash, lastSegmentStart, lines[0].Sequence == 1);
    }
}
