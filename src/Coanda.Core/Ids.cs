using UUIDNext;

namespace Coanda.Core;

public static class Ids
{
    /// <summary>Time-ordered UUIDv7, so streams and rows sort by creation without a second column.</summary>
    public static Guid New() => Uuid.NewDatabaseFriendly(Database.SQLite);

    /// <summary>
    /// The last eight hex characters, the way an operator refers to a record by hand. The last
    /// characters, not the first: a UUIDv7 opens with its timestamp, so records created in the
    /// same moment share a prefix and differ only at the end.
    /// </summary>
    public static string Short(Guid id) => id.ToString("N")[^8..];

    /// <summary>Whether a typed fragment names this id: its short form, a suffix of it, or a prefix of the full id.</summary>
    public static bool Matches(Guid id, string fragment)
    {
        string full = id.ToString("N");
        return full.EndsWith(fragment, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(fragment, StringComparison.OrdinalIgnoreCase);
    }
}
