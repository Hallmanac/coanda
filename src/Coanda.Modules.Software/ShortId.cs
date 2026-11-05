namespace Coanda.Modules.Software;

/// <summary>How the module refers to a record by hand, the way the core does.</summary>
public static class ShortId
{
    /// <summary>The last eight hex characters of <paramref name="id"/>, which a parcel's branch name carries.</summary>
    public static string Of(Guid id) => id.ToString("N")[^8..];
}
