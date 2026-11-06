using System.Security.Cryptography;

namespace Coanda.Core.Identity;

/// <summary>
/// A public key's fingerprint, the one printable form design section 6 names: <c>sha256:</c> and the full
/// SHA-256 digest of the key's 32 bytes in lowercase hex, 64 digits, never truncated. <c>coa node show</c>
/// prints it and <c>coa join</c> reads exactly it, so a person carries it from the owner's node to the
/// joining one and nothing on the remote supplies it.
/// </summary>
public static class KeyFingerprint
{
    public const string Prefix = "sha256:";

    /// <summary>The form a fingerprint must have, as a rule quotes it.</summary>
    public const string Rule = "'sha256:' and the 64 lowercase hex digits of the SHA-256 digest of the key's 32 bytes";

    /// <summary>The fingerprint of <paramref name="key"/>, a key in <see cref="IdentityKeys"/>' form.</summary>
    public static string Of(string key)
    {
        if (!IdentityKeys.IsKey(key))
        {
            throw new DomainException($"'{key}' is not a public key; a key is {IdentityKeys.Rule}.");
        }

        return Prefix + Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(key)));
    }

    /// <summary>Whether <paramref name="fingerprint"/> is written exactly in the one form a fingerprint has.</summary>
    public static bool IsFingerprint(string? fingerprint) =>
        fingerprint is { Length: 71 }
        && fingerprint.StartsWith(Prefix, StringComparison.Ordinal)
        && fingerprint[Prefix.Length..].All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
}
