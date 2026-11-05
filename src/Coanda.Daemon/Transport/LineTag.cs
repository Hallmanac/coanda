namespace Coanda.Daemon.Transport;

/// <summary>
/// Which of a project's repos carry an event: either the names of those repos, or project-wide,
/// which every repo of the project carries (design section 6). On the line a repo list is a JSON
/// array of the names in ordinal order with none repeated, and project-wide is the JSON string
/// <c>"project-wide"</c>, which no array, an empty one included, can equal.
/// </summary>
public sealed class LineTag
{
    /// <summary>How the line writes the project-wide tag.</summary>
    public const string ProjectWideValue = "project-wide";

    private LineTag(IReadOnlyList<string>? repos)
    {
        RepoNames = repos;
    }

    /// <summary>The tag of an event every repo of the project carries: membership, definitions, decisions.</summary>
    public static LineTag ProjectWide { get; } = new(null);

    /// <summary>The names of the repos that carry the event, in ordinal order; none when the tag is project-wide.</summary>
    public IReadOnlyList<string>? RepoNames { get; }

    public bool IsProjectWide => RepoNames is null;

    /// <summary>The tag naming <paramref name="names"/>, which are put in ordinal order; a name given twice is refused.</summary>
    public static LineTag Repos(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        string[] sorted = [.. names.Order(StringComparer.Ordinal)];
        for (int i = 0; i < sorted.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(sorted[i], nameof(names));
            if (i > 0 && sorted[i] == sorted[i - 1])
            {
                throw new ArgumentException($"The repo '{sorted[i]}' is named twice in one tag.", nameof(names));
            }
        }

        return new LineTag(sorted);
    }

    public override string ToString() =>
        RepoNames is { } names
            ? $"[{string.Join(", ", names)}]"
            : ProjectWideValue;
}
