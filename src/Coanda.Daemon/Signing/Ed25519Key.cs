using System.Security.Cryptography;
using Rfc8032 = Org.BouncyCastle.Math.EC.Rfc8032.Ed25519;

namespace Coanda.Daemon.Signing;

/// <summary>
/// An ed25519 key pair, and the one path that signs and verifies with ed25519 (RFC 8032, pure
/// Ed25519 with no context). A private key is the 32-byte seed RFC 8032 names, a public key is
/// 32 bytes and a signature 64, and each is written in standard base64 with padding, the
/// conventions <c>approval.schema.json</c> fixes. The node key signs the lines a node exports, and
/// a module approval is verified through <see cref="Verify"/> too.
/// </summary>
public sealed class Ed25519Key
{
    public const int PrivateKeyLength = 32;
    public const int PublicKeyLength = 32;
    public const int SignatureLength = 64;

    private readonly byte[] privateKey;

    private Ed25519Key(byte[] privateKey)
    {
        this.privateKey = privateKey;
        byte[] publicKey = new byte[PublicKeyLength];
        Rfc8032.GeneratePublicKey(privateKey, 0, publicKey, 0);
        PublicKey = Convert.ToBase64String(publicKey);
    }

    /// <summary>The public key in standard base64 with padding.</summary>
    public string PublicKey { get; }

    /// <summary>The private key's 32 bytes, in standard base64 with padding, which is how the node identity file holds it.</summary>
    internal string PrivateKeyText => Convert.ToBase64String(privateKey);

    /// <summary>A new key pair from the operating system's cryptographic random source.</summary>
    public static Ed25519Key Generate() => new(RandomNumberGenerator.GetBytes(PrivateKeyLength));

    /// <summary>The key pair whose private key is <paramref name="privateKey"/>'s 32 bytes.</summary>
    public static Ed25519Key FromPrivateKey(ReadOnlySpan<byte> privateKey) =>
        privateKey.Length == PrivateKeyLength
            ? new Ed25519Key(privateKey.ToArray())
            : throw new ArgumentException($"An ed25519 private key is {PrivateKeyLength} bytes, not {privateKey.Length}.", nameof(privateKey));

    /// <summary>
    /// The key pair whose private key <paramref name="privateKey"/> writes in standard base64, or
    /// none when the text is not exactly the base64 of 32 bytes.
    /// </summary>
    public static Ed25519Key? FromPrivateKeyText(string privateKey) =>
        DecodeExactly(privateKey, PrivateKeyLength) is { } bytes
            ? new Ed25519Key(bytes)
            : null;

    /// <summary>The 64-byte signature of <paramref name="message"/>.</summary>
    public byte[] Sign(ReadOnlySpan<byte> message)
    {
        byte[] signature = new byte[SignatureLength];
        byte[] bytes = message.ToArray();
        Rfc8032.Sign(privateKey, 0, bytes, 0, bytes.Length, signature, 0);
        return signature;
    }

    /// <summary>
    /// Whether <paramref name="signature"/> is the signature of <paramref name="message"/> under
    /// <paramref name="publicKey"/>. A key or signature of the wrong length never verifies.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != PublicKeyLength || signature.Length != SignatureLength)
        {
            return false;
        }

        byte[] bytes = message.ToArray();
        return Rfc8032.Verify(signature.ToArray(), 0, publicKey.ToArray(), 0, bytes, 0, bytes.Length);
    }

    /// <summary>
    /// The bytes <paramref name="text"/> writes in standard base64 with padding when they are exactly
    /// <paramref name="length"/> long and the text is the one way to write them; otherwise none.
    /// </summary>
    public static byte[]? DecodeExactly(string text, int length)
    {
        byte[] bytes = new byte[length];
        return Convert.TryFromBase64String(text, bytes, out int written)
            && written == length
            && Convert.ToBase64String(bytes) == text
                ? bytes
                : null;
    }
}
