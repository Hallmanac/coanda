using System.Text;
using Coanda.Daemon.Signing;
using Coanda.Daemon.Store;
using Coanda.Daemon.Transport;

namespace Coanda.Tests.Daemon.Transport;

/// <summary>Builds signed lines and runs of them for the line and chain tests, and rewrites a line's text and signs it again.</summary>
internal static class Lines
{
    public static readonly Guid Project = Guid.Parse("0196f3a0-0000-7000-8000-000000000001");
    public static readonly Guid Origin = Guid.Parse("0196f3a0-0000-7000-8000-0000000000aa");
    public static readonly Guid Stream = Guid.Parse("0196f3a0-0000-7000-8000-0000000000bb");
    public static readonly Guid Event = Guid.Parse("0196f3a0-0000-7000-8000-0000000000cc");
    public static readonly DateTimeOffset Recorded = new(2026, 11, 5, 6, 0, 19, 554, TimeSpan.Zero);

    /// <summary>The secret key of RFC 8032's first ed25519 test vector.</summary>
    public static readonly Ed25519Key Key = Ed25519Key.FromPrivateKey(Convert.FromHexString("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60"));

    public static readonly Ed25519Key OtherKey = Ed25519Key.FromPrivateKey(Convert.FromHexString("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb"));

    public static IReadOnlyCollection<string> Accepted => [Key.PublicKey];

    public static LineContent Content(long sequence, string? previous, string data = """{"title":"Line"}""") =>
        new(Project, Origin, sequence, previous, LineTag.Repos(["coanda"]), Stream, Event, Recorded, new StoredEvent("parcel_created", data));

    /// <summary>A run of <paramref name="count"/> lines from the origin's first, each naming the hash of the one before it.</summary>
    public static List<byte[]> Run(int count, Ed25519Key? key = null)
    {
        var lines = new List<byte[]>();
        string? previous = null;
        for (int sequence = 1; sequence <= count; sequence++)
        {
            byte[] line = SignedLine.Sign(Content(sequence, previous, $$"""{"n":{{sequence}}}"""), key ?? Key);
            lines.Add(line);
            previous = SignedLine.HashOf(line);
        }

        return lines;
    }

    public static string Text(byte[] line) => Encoding.UTF8.GetString(line);

    /// <summary>
    /// <paramref name="line"/> with <paramref name="from"/> replaced by <paramref name="to"/>, once, and
    /// the signature kept as it was. The test fails when the text does not occur exactly once.
    /// </summary>
    public static byte[] Replace(byte[] line, string from, string to)
    {
        string text = Text(line);
        int at = text.IndexOf(from, StringComparison.Ordinal);
        Assert.True(at >= 0 && text.IndexOf(from, at + 1, StringComparison.Ordinal) < 0, $"'{from}' occurs exactly once in {text}");
        return Encoding.UTF8.GetBytes(string.Concat(text.AsSpan(0, at), to, text.AsSpan(at + from.Length)));
    }

    /// <summary>
    /// <paramref name="line"/> with <paramref name="from"/> replaced by <paramref name="to"/> and signed
    /// again with <see cref="Key"/>, so a line no signer would write still carries a good signature.
    /// </summary>
    public static byte[] Resign(byte[] line, string from, string to)
    {
        string text = Text(Replace(line, from, to));
        const string marker = ",\"signature\":";
        string unsigned = string.Concat(text.AsSpan(0, text.LastIndexOf(marker, StringComparison.Ordinal)), "}");
        byte[] signature = Key.Sign(Encoding.UTF8.GetBytes(unsigned));
        return Encoding.UTF8.GetBytes($"{unsigned[..^1]}{marker}\"{Convert.ToBase64String(signature)}\"}}");
    }
}
