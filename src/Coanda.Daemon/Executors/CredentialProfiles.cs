namespace Coanda.Daemon.Executors;

/// <summary>
/// The credential profiles the daemon's configuration held when it started, looked up by vendor and
/// name. Names are compared ordinally after trimming, as the configuration compares them.
/// </summary>
public sealed class CredentialProfiles(IReadOnlyList<CredentialProfile> profiles)
{
    public IReadOnlyList<CredentialProfile> All { get; } = profiles;

    /// <summary>The distinct profile names held under any vendor.</summary>
    public IReadOnlyCollection<string> Names => [.. All.Select(p => p.Name).Distinct(StringComparer.Ordinal)];

    /// <summary>The profile named <paramref name="name"/> under <paramref name="vendor"/>, or null when none is held.</summary>
    public CredentialProfile? Find(string vendor, string name)
    {
        string trimmed = name.Trim();
        return All.FirstOrDefault(p =>
            string.Equals(p.Vendor, vendor, StringComparison.Ordinal) && string.Equals(p.Name, trimmed, StringComparison.Ordinal));
    }
}
