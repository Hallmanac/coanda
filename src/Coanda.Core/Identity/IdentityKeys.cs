namespace Coanda.Core.Identity;

/// <summary>The form of a public key in the identity records: 32 bytes in standard base64 with padding.</summary>
public static class IdentityKeys
{
    public const int Length = 32;

    /// <summary>The form a key must have, as a rule quotes it.</summary>
    public const string Rule = "32 bytes in standard base64 with padding";

    /// <summary>Whether <paramref name="key"/> is 32 bytes in standard base64 with padding, written the one way that form allows.</summary>
    public static bool IsKey(string? key)
    {
        if (key is null)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[Length + 3];
        return Convert.TryFromBase64String(key, bytes, out int written)
            && written == Length
            && string.Equals(Convert.ToBase64String(bytes[..Length]), key, StringComparison.Ordinal);
    }
}
