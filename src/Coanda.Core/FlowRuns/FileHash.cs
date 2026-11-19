using System.Security.Cryptography;

namespace Coanda.Core.FlowRuns;

/// <summary>
/// The hash an agent step's event records for a file its session left on the node: <see cref="Prefix"/>
/// and the full SHA-256 digest of the file's uncompressed bytes in lowercase hex, the form the claim
/// ledger checks a line's hash in. It is for checking a file against its event, so it is never cut short.
/// </summary>
public static class FileHash
{
    public const string Prefix = "sha256:";

    /// <summary>The hash of everything left to read in <paramref name="content"/>.</summary>
    public static string Of(Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return Prefix + Convert.ToHexStringLower(SHA256.HashData(content));
    }

    /// <summary>The hash of <paramref name="bytes"/>.</summary>
    public static string Of(ReadOnlySpan<byte> bytes) => Prefix + Convert.ToHexStringLower(SHA256.HashData(bytes));

    /// <summary>Whether <paramref name="hash"/> is written the way <see cref="Of(Stream)"/> writes one.</summary>
    public static bool IsHash(string hash) =>
        hash.Length == Prefix.Length + 64
        && hash.StartsWith(Prefix, StringComparison.Ordinal)
        && !hash.AsSpan(Prefix.Length).ContainsAnyExcept("0123456789abcdef");
}
