using System.Text;

namespace Coanda.Daemon.Transport;

/// <summary>
/// An origin's ledger in one project as the files of its ref: one file per segment, named
/// <c>yyyy-MM.jsonl</c>, holding the segment's lines in sequence order, each ended by a line feed. They are
/// made from the store's records alone, so the same records always give the same bytes.
/// </summary>
public sealed class LedgerSegments
{
    public const string Extension = ".jsonl";

    private LedgerSegments(IReadOnlyDictionary<string, byte[]> files, string lastHash)
    {
        Files = files;
        LastHash = lastHash;
    }

    /// <summary>Each file's bytes by its name, in name order.</summary>
    public IReadOnlyDictionary<string, byte[]> Files { get; }

    /// <summary>The hash of the chain's last line, which names this ledger as a whole.</summary>
    public string LastHash { get; }

    /// <summary>The files of <paramref name="lines"/>, which are one origin's, in sequence order, and not empty.</summary>
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

        return new LedgerSegments(files, lines[^1].Hash);
    }
}
