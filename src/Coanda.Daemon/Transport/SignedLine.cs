using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Coanda.Daemon.Signing;
using Coanda.Daemon.Store;

namespace Coanda.Daemon.Transport;

/// <summary>
/// What one line says about one event: the project it belongs to, by the id its registration
/// records; the origin node that authored it; the origin's sequence in that project, from 1; the
/// hash of the origin's previous line in that project, none for its first; the repos that carry it;
/// its stream; the id the store gave the event; the time the store recorded it; and the event as the
/// events table holds it. The stream, the event id, and the recorded time are what an importer appends
/// the event with, so it lands in the same stream as the same event.
/// </summary>
public sealed record LineContent(
    Guid ProjectId,
    Guid Origin,
    long Sequence,
    string? PreviousHash,
    LineTag Tag,
    Guid StreamId,
    Guid EventId,
    DateTimeOffset RecordedAt,
    StoredEvent Event);

/// <summary>
/// One signed line: a project event as one line of JSONL, signed by its origin's node key. Its
/// members, in this order, are <c>format</c>, <c>project</c>, <c>origin</c>, <c>sequence</c>,
/// <c>previous</c>, <c>tag</c>, <c>stream</c>, <c>event</c>, <c>recorded</c>, <c>type</c>,
/// <c>data</c>, <c>signer</c>, and <c>signature</c>; design section 6 states each one's encoding. The
/// signature is an ed25519 signature over the UTF-8 bytes of the line with its signature member
/// removed, that is, the line up to the comma before <c>"signature"</c>, closed with <c>}</c>. A line
/// has exactly one way to be written, and one written any other way does not parse, so a line's bytes
/// are a function of what it says.
/// </summary>
/// <remarks>
/// The line is not written in the JSON Canonicalization Scheme (RFC 8785), which the approval record
/// uses, because that scheme writes every number through an IEEE double, and event data can hold any
/// number: 9007199254740993 and 9007199254740992 would canonicalize alike and a change between them
/// would still verify. The data is instead the stored JSON text exactly, with only the whitespace
/// outside its strings removed, so every number and every string keeps the characters it was stored
/// with. The other members' strings are escaped as RFC 8785 escapes them.
/// </remarks>
public sealed class SignedLine
{
    /// <summary>The format this build writes and reads. A later format is read beside it, by its own number.</summary>
    public const int FormatVersion = 1;

    /// <summary>How a line's hash is written: this prefix, then the SHA-256 digest of the line in lowercase hex.</summary>
    public const string HashPrefix = "sha256:";

    /// <summary>How a line writes the recorded time: UTC, to the tenth of a microsecond, always with every digit.</summary>
    public const string RecordedFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    /// <summary>How deep event data may nest; far past anything the store's serializer writes, so no event is refused for its depth.</summary>
    private const int MaxDepth = 4096;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>The members after <c>format</c>, which every line of this format has.</summary>
    private static readonly string[] Members = ["project", "origin", "sequence", "previous", "tag", "stream", "event", "recorded", "type", "data", "signer", "signature"];

    private SignedLine(LineContent content, byte[] signer, byte[] signature, byte[] bytes, byte[] signedBytes)
    {
        Content = content;
        SignerKey = signer;
        SignatureBytes = signature;
        Bytes = bytes;
        SignedBytes = signedBytes;
        Hash = HashOf(bytes);
    }

    public LineContent Content { get; }

    /// <summary>The signer's ed25519 public key in standard base64 with padding.</summary>
    public string Signer => Convert.ToBase64String(SignerKey);

    /// <summary>The signature in standard base64 with padding.</summary>
    public string Signature => Convert.ToBase64String(SignatureBytes);

    /// <summary>The line's hash: <see cref="HashPrefix"/> and the full SHA-256 digest of <see cref="Bytes"/>, never truncated.</summary>
    public string Hash { get; }

    /// <summary>The line exactly as written, with no line ending.</summary>
    public byte[] Bytes { get; }

    private byte[] SignerKey { get; }

    private byte[] SignatureBytes { get; }

    /// <summary>What the signature covers: the line without its signature member.</summary>
    private byte[] SignedBytes { get; }

    /// <summary>Whether the signature is the signer's over every other member of the line.</summary>
    public bool SignatureVerifies => Ed25519Key.Verify(SignerKey, SignedBytes, SignatureBytes);

    /// <summary>
    /// The line saying <paramref name="content"/>, signed with <paramref name="key"/>: its bytes, with
    /// no line ending. Throws when the content is not something a line can say: a sequence below 1, a
    /// first line naming a previous hash or a later one naming none, a previous hash not written as
    /// <see cref="HashOf"/> writes one, an empty type name, or data that is not JSON.
    /// </summary>
    public static byte[] Sign(LineContent content, Ed25519Key key)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(key);
        if (Fault(content) is { } fault)
        {
            throw new ArgumentException(fault, nameof(content));
        }

        byte[] data;
        try
        {
            data = CompactData(StrictUtf8.GetBytes(content.Event.Data));
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        {
            throw new ArgumentException($"The event's data is not JSON: {e.Message}", nameof(content), e);
        }

        byte[] signedBytes = Unsigned(content, data, Convert.FromBase64String(key.PublicKey));
        return Signed(signedBytes, key.Sign(signedBytes));
    }

    /// <summary>
    /// The line <paramref name="line"/> holds, with no line ending, or none when it does not parse as
    /// a line, with <paramref name="fault"/> saying why. Parsing checks the line's shape alone; whether
    /// its signature verifies is <see cref="SignatureVerifies"/>.
    /// </summary>
    public static SignedLine? Parse(ReadOnlySpan<byte> line, out string? fault)
    {
        byte[] bytes = line.ToArray();
        try
        {
            StrictUtf8.GetString(bytes);
        }
        catch (ArgumentException)
        {
            fault = "it is not UTF-8";
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = MaxDepth });
            return Read(document.RootElement, bytes, out fault);
        }
        catch (JsonException e)
        {
            fault = $"it is not JSON ({e.Message})";
            return null;
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or ArgumentException)
        {
            fault = $"a member does not read as its kind ({e.Message})";
            return null;
        }
    }

    /// <summary>The hash of a line: <see cref="HashPrefix"/> and the full SHA-256 digest of its bytes as written, signature included, in lowercase hex.</summary>
    public static string HashOf(ReadOnlySpan<byte> line) => HashPrefix + Convert.ToHexStringLower(SHA256.HashData(line));

    /// <summary>Whether <paramref name="hash"/> is written the way <see cref="HashOf"/> writes one.</summary>
    public static bool IsHash(string hash) =>
        hash.Length == HashPrefix.Length + 64
        && hash.StartsWith(HashPrefix, StringComparison.Ordinal)
        && hash.AsSpan(HashPrefix.Length).ContainsAnyExcept("0123456789abcdef") is false;

    /// <summary>What a signer refuses to write: whatever a line cannot say, and a previous hash that cannot stand where the sequence puts it.</summary>
    private static string? Fault(LineContent content) =>
        content switch
        {
            { Sequence: 1, PreviousHash: not null } => "The origin's first line in a project names no previous hash.",
            { Sequence: > 1, PreviousHash: null } => $"Line {content.Sequence} must name the hash of the line before it.",
            _ => ShapeFault(content),
        };

    /// <summary>
    /// What a line cannot say at all. A first line naming a previous hash, or a later one naming none,
    /// still parses, so that verification reports it as a wrong previous hash.
    /// </summary>
    private static string? ShapeFault(LineContent content) =>
        content switch
        {
            { Sequence: < 1 } => $"A line's sequence starts at 1, not {content.Sequence}.",
            { PreviousHash: { } previous } when !IsHash(previous) => $"The previous hash '{previous}' is not '{HashPrefix}' and 64 lowercase hex digits.",
            { Event.Type: null or "" } => "An event has a type name.",
            { Event.Data: null } => "An event has data.",
            { Tag: null } => "A line has a tag.",
            _ => null,
        };

    private static SignedLine? Read(JsonElement root, byte[] bytes, out string? fault)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            fault = "it is not a JSON object";
            return null;
        }

        if (!root.TryGetProperty("format", out JsonElement format) || format.ValueKind != JsonValueKind.Number || !format.TryGetInt32(out int version))
        {
            fault = "it names no format version";
            return null;
        }

        if (version != FormatVersion)
        {
            fault = $"its format version {format.GetRawText()} is not one this build reads";
            return null;
        }

        string[] missing = [.. Members.Where(name => !root.TryGetProperty(name, out _))];
        if (missing.Length > 0)
        {
            fault = $"it has no {string.Join(", ", missing)}";
            return null;
        }

        JsonElement previous = root.GetProperty("previous");
        JsonElement tag = root.GetProperty("tag");
        var content = new LineContent(
            root.GetProperty("project").GetGuid(),
            root.GetProperty("origin").GetGuid(),
            root.GetProperty("sequence").GetInt64(),
            previous.ValueKind == JsonValueKind.Null ? null : previous.GetString(),
            tag.ValueKind == JsonValueKind.String && tag.GetString() == LineTag.ProjectWideValue
                ? LineTag.ProjectWide
                : LineTag.Repos([.. tag.EnumerateArray().Select(name => name.GetString() ?? throw new FormatException("A repo name in the tag is not a string."))]),
            root.GetProperty("stream").GetGuid(),
            root.GetProperty("event").GetGuid(),
            DateTimeOffset.ParseExact(
                root.GetProperty("recorded").GetString() ?? throw new FormatException("The recorded time is not a string."),
                RecordedFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal),
            new StoredEvent(root.GetProperty("type").GetString() ?? "", root.GetProperty("data").GetRawText()));
        if (ShapeFault(content) is { } contentFault)
        {
            fault = contentFault;
            return null;
        }

        byte[]? signer = root.GetProperty("signer").GetString() is { } signerText ? Ed25519Key.DecodeExactly(signerText, Ed25519Key.PublicKeyLength) : null;
        byte[]? signature = root.GetProperty("signature").GetString() is { } signatureText ? Ed25519Key.DecodeExactly(signatureText, Ed25519Key.SignatureLength) : null;
        if (signer is null || signature is null)
        {
            fault = signer is null
                ? $"its signer is not a {Ed25519Key.PublicKeyLength}-byte key in standard base64"
                : $"its signature is not {Ed25519Key.SignatureLength} bytes in standard base64";
            return null;
        }

        byte[] signedBytes = Unsigned(content, CompactData(StrictUtf8.GetBytes(content.Event.Data)), signer);
        if (!Signed(signedBytes, signature).AsSpan().SequenceEqual(bytes))
        {
            fault = "it is not written the one way a line is written (member order, spacing, escapes, or a member it should not have)";
            return null;
        }

        // The line is written the one way, so its data is already the compact text the content carries.
        fault = null;
        return new SignedLine(content, signer, signature, bytes, signedBytes);
    }

    private static byte[] Unsigned(LineContent content, byte[] data, byte[] signer)
    {
        var output = new ArrayBufferWriter<byte>();
        Ascii(output, string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"format\":{FormatVersion},\"project\":\"{content.ProjectId:D}\",\"origin\":\"{content.Origin:D}\",\"sequence\":{content.Sequence},\"previous\":"));
        if (content.PreviousHash is { } previous)
        {
            Text(output, previous);
        }
        else
        {
            Ascii(output, "null");
        }

        Ascii(output, ",\"tag\":");
        if (content.Tag.RepoNames is { } repos)
        {
            Ascii(output, "[");
            for (int i = 0; i < repos.Count; i++)
            {
                Ascii(output, i == 0 ? "" : ",");
                Text(output, repos[i]);
            }

            Ascii(output, "]");
        }
        else
        {
            Text(output, LineTag.ProjectWideValue);
        }

        Ascii(output, string.Create(
            CultureInfo.InvariantCulture,
            $",\"stream\":\"{content.StreamId:D}\",\"event\":\"{content.EventId:D}\",\"recorded\":\"{content.RecordedAt.UtcDateTime.ToString(RecordedFormat, CultureInfo.InvariantCulture)}\",\"type\":"));
        Text(output, content.Event.Type);
        Ascii(output, ",\"data\":");
        output.Write(data);
        Ascii(output, ",\"signer\":");
        Text(output, Convert.ToBase64String(signer));
        Ascii(output, "}");
        return output.WrittenSpan.ToArray();
    }

    /// <summary>The line: the unsigned bytes with their closing brace replaced by the signature member and a closing brace.</summary>
    private static byte[] Signed(byte[] signedBytes, byte[] signature)
    {
        var output = new ArrayBufferWriter<byte>();
        output.Write(signedBytes.AsSpan(0, signedBytes.Length - 1));
        Ascii(output, ",\"signature\":");
        Text(output, Convert.ToBase64String(signature));
        Ascii(output, "}");
        return output.WrittenSpan.ToArray();
    }

    private static void Ascii(ArrayBufferWriter<byte> output, string text) => output.Write(Encoding.ASCII.GetBytes(text));

    /// <summary>A JSON string escaped as RFC 8785 escapes one: a quote, a backslash, and the control characters, nothing else.</summary>
    private static void Text(ArrayBufferWriter<byte> output, string value)
    {
        Ascii(output, "\"");
        foreach (byte b in StrictUtf8.GetBytes(value))
        {
            string? escape = b switch
            {
                (byte)'"' => "\\\"",
                (byte)'\\' => "\\\\",
                (byte)'\b' => "\\b",
                (byte)'\f' => "\\f",
                (byte)'\n' => "\\n",
                (byte)'\r' => "\\r",
                (byte)'\t' => "\\t",
                < 0x20 => $"\\u{b:x4}",
                _ => null,
            };
            if (escape is null)
            {
                output.Write([b]);
            }
            else
            {
                Ascii(output, escape);
            }
        }

        Ascii(output, "\"");
    }

    /// <summary>
    /// JSON text with the whitespace outside its strings removed and every token kept as it was
    /// written: a number's digits, and a string's or a member name's characters and escapes, exactly.
    /// </summary>
    private static byte[] CompactData(byte[] json)
    {
        var output = new ArrayBufferWriter<byte>(json.Length);
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = MaxDepth });
        bool separate = false;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject or JsonTokenType.StartArray:
                    Separate(output, separate);
                    output.Write([reader.TokenType == JsonTokenType.StartObject ? (byte)'{' : (byte)'[']);
                    separate = false;
                    break;
                case JsonTokenType.EndObject or JsonTokenType.EndArray:
                    output.Write([reader.TokenType == JsonTokenType.EndObject ? (byte)'}' : (byte)']']);
                    separate = true;
                    break;
                case JsonTokenType.PropertyName:
                    Separate(output, separate);
                    Quoted(output, reader.ValueSpan);
                    output.Write([(byte)':']);
                    separate = false;
                    break;
                case JsonTokenType.String:
                    Separate(output, separate);
                    Quoted(output, reader.ValueSpan);
                    separate = true;
                    break;
                default:
                    Separate(output, separate);
                    output.Write(reader.ValueSpan);
                    separate = true;
                    break;
            }
        }

        return output.WrittenSpan.ToArray();

        static void Separate(ArrayBufferWriter<byte> output, bool separate)
        {
            if (separate)
            {
                output.Write([(byte)',']);
            }
        }

        static void Quoted(ArrayBufferWriter<byte> output, ReadOnlySpan<byte> raw)
        {
            output.Write([(byte)'"']);
            output.Write(raw);
            output.Write([(byte)'"']);
        }
    }
}
