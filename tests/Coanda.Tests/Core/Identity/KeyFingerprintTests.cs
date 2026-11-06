using System.Security.Cryptography;
using Coanda.Core;
using Coanda.Core.Identity;
using static Coanda.Tests.Core.Identity.Parties;

namespace Coanda.Tests.Core.Identity;

public sealed class KeyFingerprintTests
{
    [Fact]
    public void A_fingerprint_is_sha256_and_the_full_lowercase_hex_digest_of_the_keys_32_bytes()
    {
        string expected = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Convert.FromBase64String(A.Key)));

        Assert.Equal(expected, KeyFingerprint.Of(A.Key));
        Assert.Equal(71, expected.Length);
        Assert.True(KeyFingerprint.IsFingerprint(expected));
        Assert.NotEqual(KeyFingerprint.Of(A.Key), KeyFingerprint.Of(B.Key));
    }

    [Fact]
    public void Only_the_one_printable_form_reads_as_a_fingerprint()
    {
        string fingerprint = KeyFingerprint.Of(A.Key);

        Assert.False(KeyFingerprint.IsFingerprint(fingerprint.ToUpperInvariant()));
        Assert.False(KeyFingerprint.IsFingerprint(fingerprint[..^1]));
        Assert.False(KeyFingerprint.IsFingerprint(fingerprint["sha256:".Length..]));
        Assert.False(KeyFingerprint.IsFingerprint($" {fingerprint}"));
        Assert.False(KeyFingerprint.IsFingerprint(null));
    }

    [Fact]
    public void A_fingerprint_is_of_a_key_only()
    {
        Assert.Throws<DomainException>(() => KeyFingerprint.Of("not a key"));
    }
}
