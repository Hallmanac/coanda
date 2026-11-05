using System.Security.Cryptography;
using System.Text;
using Coanda.Daemon.Signing;
using Coanda.Daemon.Store;
using Coanda.Daemon.Transport;

namespace Coanda.Tests.Daemon.Transport;

/// <summary>
/// The signed line: what it carries, that its signature covers every member but itself, that its
/// bytes are one function of what it says on every platform, and that its hash is the full digest.
/// </summary>
public sealed class SignedLineTests
{
    /// <summary>
    /// A line signed with <see cref="Lines.Key"/>, held here in the test source as one line of text, so
    /// no checkout's line-ending conversion reaches it, and written out by hand to the expected bytes.
    /// Its data was stored indented, holds an integer past a double's exact range, a decimal of 20
    /// significant digits, a raw non-ASCII character, and escapes, all of which the line keeps as stored.
    /// </summary>
    private const string FixtureLine =
        """{"format":1,"project":"0196f3a0-1c2d-7e4f-8a5b-6c7d8e9f0a1b","origin":"0196f3a0-2b3c-7d4e-9f50-61728394a5b6","sequence":3,"previous":"sha256:000000000000000000000000000000000123456789abcdef0123456789abcdef","tag":["coanda","web"],"stream":"0196f3a0-3c4d-7e5f-a061-728394a5b6c7","event":"0196f3a0-5e6f-7a71-8283-94a5b6c7d8e9","recorded":"2026-11-05T12:00:00.1234567Z","type":"agent_step_answered","data":{"flowRunId":"0196f3a0-4d5e-7f60-b172-8394a5b6c7d8","answer":{"count":9007199254740993,"ratio":1.2345678901234567890,"note":"caf\u00e9 \"quoted\" \u00e9"},"answeredAt":"2026-11-05T12:00:00+00:00"},"signer":"11qYAYKxCrfVS/7TyWQHOg7hcvPapiMlrwIaaPcHURo=","signature":"/Wjv/EWHq5Z7cLf/0KQaIYwYmYvpfep0p7FmAwp9/BqYC9DJU3Yaqz/ylw2N6zmHFXdzCJ8778kxQHyX69UeAg=="}""";

    /// <summary>The fixture line's hash, so a fixture changed in the source by anything at all fails here before it fails anywhere else.</summary>
    private const string FixtureHash = "sha256:7ea0c7e960f011d5f2182ed19a07bac5efddd7c8411941cd50bd0528b0051d1c";

    /// <summary>The fixture's event as the events table would hold it: indented, with a raw é in one string and an escaped one beside it.</summary>
    private static readonly LineContent FixtureContent = new(
        Guid.Parse("0196f3a0-1c2d-7e4f-8a5b-6c7d8e9f0a1b"),
        Guid.Parse("0196f3a0-2b3c-7d4e-9f50-61728394a5b6"),
        3,
        "sha256:000000000000000000000000000000000123456789abcdef0123456789abcdef",
        LineTag.Repos(["web", "coanda"]),
        Guid.Parse("0196f3a0-3c4d-7e5f-a061-728394a5b6c7"),
        Guid.Parse("0196f3a0-5e6f-7a71-8283-94a5b6c7d8e9"),
        new DateTimeOffset(2026, 11, 5, 7, 0, 0, TimeSpan.FromHours(-5)).AddTicks(1234567),
        new StoredEvent(
            "agent_step_answered",
            "{\n  \"flowRunId\": \"0196f3a0-4d5e-7f60-b172-8394a5b6c7d8\",\n"
            + "  \"answer\": { \"count\": 9007199254740993, \"ratio\": 1.2345678901234567890, \"note\": \"caf\u00e9 \\\"quoted\\\" \\u00e9\" },\n"
            + "  \"answeredAt\": \"2026-11-05T12:00:00+00:00\"\n}"));

    /// <summary>
    /// The fixture as bytes. The raw string holds <c>\u00e9</c> as six characters, the escape the data
    /// keeps, and the one raw é the data holds is put in here, so the source stays ASCII.
    /// </summary>
    private static byte[] Fixture => Encoding.UTF8.GetBytes(FixtureLine.Replace("caf\\u00e9", "caf\u00e9", StringComparison.Ordinal));

    [Fact]
    public void Signing_the_fixtures_event_with_its_key_gives_the_fixture_lines_bytes_and_the_fixture_verifies()
    {
        Assert.Equal(FixtureHash, SignedLine.HashOf(Fixture));

        byte[] signed = SignedLine.Sign(FixtureContent, Lines.Key);

        Assert.Equal(Fixture, signed);
        SignedLine line = Parsed(Fixture);
        Assert.True(line.SignatureVerifies);
        Assert.Equal(FixtureHash, line.Hash);
        Assert.Equal(Lines.Key.PublicKey, line.Signer);
        Assert.Equal(["coanda", "web"], line.Content.Tag.RepoNames);
        Assert.Equal("agent_step_answered", line.Content.Event.Type);
        Assert.Contains("\"count\":9007199254740993,\"ratio\":1.2345678901234567890,", line.Content.Event.Data, StringComparison.Ordinal);
    }

    [Fact]
    public void A_lines_hash_is_the_full_sha256_digest_of_the_whole_line_signature_included()
    {
        byte[] line = Lines.Run(1)[0];

        string hash = Parsed(line).Hash;

        Assert.Equal("sha256:" + Convert.ToHexStringLower(SHA256.HashData(line)), hash);
        Assert.Equal("sha256:".Length + 64, hash.Length);
        Assert.Contains(",\"signature\":\"", Lines.Text(line), StringComparison.Ordinal);
    }

    [Fact]
    public void The_signature_covers_the_line_up_to_its_signature_member()
    {
        string text = Lines.Text(Lines.Run(1)[0]);
        int at = text.IndexOf(",\"signature\":", StringComparison.Ordinal);
        byte[] unsigned = Encoding.UTF8.GetBytes(text[..at] + "}");
        byte[] signature = Convert.FromBase64String(text[(at + ",\"signature\":\"".Length)..^2]);

        Assert.True(Ed25519Key.Verify(Convert.FromBase64String(Lines.Key.PublicKey), unsigned, signature));
    }

    public static TheoryData<string, string, string> SignedFields => new()
    {
        { "project", "0196f3a0-1c2d-7e4f-8a5b-6c7d8e9f0a1b", "0196f3a0-1c2d-7e4f-8a5b-6c7d8e9f0a1c" },
        { "origin", "0196f3a0-2b3c-7d4e-9f50-61728394a5b6", "0196f3a0-2b3c-7d4e-9f50-61728394a5b7" },
        { "sequence", "\"sequence\":3", "\"sequence\":4" },
        { "previous", "0123456789abcdef0123456789abcdef\"", "0123456789abcdef0123456789abcdee\"" },
        { "tag", "\"tag\":[\"coanda\",\"web\"]", "\"tag\":[\"coanda\",\"wec\"]" },
        { "stream", "0196f3a0-3c4d-7e5f-a061-728394a5b6c7", "0196f3a0-3c4d-7e5f-a061-728394a5b6c8" },
        { "event", "0196f3a0-5e6f-7a71-8283-94a5b6c7d8e9", "0196f3a0-5e6f-7a71-8283-94a5b6c7d8ea" },
        { "recorded", "12:00:00.1234567Z", "12:00:00.1234568Z" },
        { "type", "agent_step_answered", "agent_step_answerer" },
        { "data, an integer past a double's exact range", "9007199254740993", "9007199254740992" },
        { "data, the last digit of a 20-digit decimal", "1.2345678901234567890", "1.2345678901234567891" },
        { "data, a string", "\"quoted\\\"", "\"quotes\\\"" },
        { "signer", "11qYAYKxCrfVS/7TyWQHOg7hcvPapiMlrwIaaPcHURo=", "PUAXw+hDiVqStwqnTRt+vJyYLM8uxJaMwM1V8Sr0Zgw=" },
    };

    [Theory]
    [MemberData(nameof(SignedFields))]
    public void Changing_any_one_signed_field_of_a_valid_line_fails_its_signature(string field, string from, string to)
    {
        byte[] changed = Lines.Replace(Fixture, from, to);

        SignedLine line = Parsed(changed);

        Assert.False(line.SignatureVerifies, $"the line verifies with its {field} changed");
    }

    [Fact]
    public void Changing_the_format_version_leaves_a_line_this_build_does_not_read()
    {
        byte[] changed = Lines.Replace(Fixture, "\"format\":1,", "\"format\":2,");

        Assert.Null(SignedLine.Parse(changed, out string? fault));
        Assert.Contains("format version 2", fault, StringComparison.Ordinal);
    }

    [Fact]
    public void A_tag_naming_repos_in_other_than_ordinal_order_or_written_any_other_way_does_not_parse()
    {
        // re-signed, so only the shape is wrong
        byte[] unordered = Lines.Resign(Fixture, "[\"coanda\",\"web\"]", "[\"web\",\"coanda\"]");
        byte[] spaced = Lines.Resign(Fixture, "\"sequence\":3", "\"sequence\": 3");
        byte[] upper = Lines.Resign(Fixture, "0196f3a0-3c4d-7e5f-a061-728394a5b6c7", "0196F3A0-3C4D-7E5F-A061-728394A5B6C7");
        byte[] extra = Lines.Resign(Fixture, "\"format\":1,", "\"format\":1,\"note\":\"x\",");
        byte[] crlf = [.. Fixture, (byte)'\r'];

        foreach (byte[] line in (byte[][])[unordered, spaced, upper, extra, crlf])
        {
            Assert.Null(SignedLine.Parse(line, out string? fault));
            Assert.NotNull(fault);
        }
    }

    [Fact]
    public void The_project_wide_tag_is_not_any_list_of_repo_names_an_empty_one_included()
    {
        LineContent content = Lines.Content(1, null);
        string projectWide = Lines.Text(SignedLine.Sign(content with { Tag = LineTag.ProjectWide }, Lines.Key));
        string empty = Lines.Text(SignedLine.Sign(content with { Tag = LineTag.Repos([]) }, Lines.Key));
        string named = Lines.Text(SignedLine.Sign(content with { Tag = LineTag.Repos([LineTag.ProjectWideValue]) }, Lines.Key));

        Assert.Contains("\"tag\":\"project-wide\"", projectWide, StringComparison.Ordinal);
        Assert.Contains("\"tag\":[]", empty, StringComparison.Ordinal);
        Assert.Contains("\"tag\":[\"project-wide\"]", named, StringComparison.Ordinal);
        Assert.True(Parsed(Encoding.UTF8.GetBytes(projectWide)).Content.Tag.IsProjectWide);
        Assert.False(Parsed(Encoding.UTF8.GetBytes(empty)).Content.Tag.IsProjectWide);
        Assert.False(Parsed(Encoding.UTF8.GetBytes(named)).Content.Tag.IsProjectWide);
    }

    [Theory]
    [InlineData("123456789012345678901234567890123456789012345678901234567890")]
    [InlineData("-0.000000000000000000000000000000000000000012345678901234567890123")]
    [InlineData("1e400")]
    [InlineData("1.0E+2")]
    [InlineData("-0")]
    public void No_event_is_refused_for_the_size_or_precision_of_a_number_and_the_number_is_kept_as_stored(string number)
    {
        byte[] signed = SignedLine.Sign(Lines.Content(1, null, $$"""{"n": [ {{number}} ]}"""), Lines.Key);

        SignedLine line = Parsed(signed);

        Assert.True(line.SignatureVerifies);
        Assert.Equal($$"""{"n":[{{number}}]}""", line.Content.Event.Data);
    }

    [Fact]
    public void Data_stored_across_several_lines_is_written_on_one_line_with_its_strings_untouched()
    {
        string data = "{\r\n\t\"a\" : \"  two  spaces \\n and an escape \\u2028\",\n \"b\" : [ 1 , { } , [ ] , null, true ]\n}";

        byte[] signed = SignedLine.Sign(Lines.Content(1, null, data), Lines.Key);

        Assert.DoesNotContain((byte)'\n', signed);
        Assert.DoesNotContain((byte)'\r', signed);
        Assert.Equal("""{"a":"  two  spaces \n and an escape \u2028","b":[1,{},[],null,true]}""", Parsed(signed).Content.Event.Data);
    }

    [Fact]
    public void A_signer_refuses_what_a_line_cannot_say()
    {
        Assert.Throws<ArgumentException>(() => SignedLine.Sign(Lines.Content(0, null), Lines.Key));
        Assert.Throws<ArgumentException>(() => SignedLine.Sign(Lines.Content(1, FixtureHash), Lines.Key));
        Assert.Throws<ArgumentException>(() => SignedLine.Sign(Lines.Content(2, null), Lines.Key));
        Assert.Throws<ArgumentException>(() => SignedLine.Sign(Lines.Content(2, "sha256:abc"), Lines.Key));
        Assert.Throws<ArgumentException>(() => SignedLine.Sign(Lines.Content(1, null, "{not json"), Lines.Key));
        Assert.Throws<ArgumentException>(() => LineTag.Repos(["web", "web"]));
    }

    [Fact]
    public void The_ed25519_key_signs_as_rfc_8032s_test_vectors_do()
    {
        Ed25519Key second = Lines.OtherKey;
        byte[] message = [0x72];

        byte[] empty = Lines.Key.Sign([]);
        byte[] signature = second.Sign(message);

        Assert.Equal(Convert.FromHexString("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a"), Convert.FromBase64String(Lines.Key.PublicKey));
        Assert.Equal(
            Convert.FromHexString("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"),
            empty);
        Assert.Equal(Convert.FromHexString("3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c"), Convert.FromBase64String(second.PublicKey));
        Assert.Equal(
            Convert.FromHexString("92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00"),
            signature);
        Assert.True(Ed25519Key.Verify(Convert.FromBase64String(second.PublicKey), message, signature));
        Assert.False(Ed25519Key.Verify(Convert.FromBase64String(second.PublicKey), [0x73], signature));
        Assert.False(Ed25519Key.Verify(Convert.FromBase64String(Lines.Key.PublicKey), message, signature));
    }

    private static SignedLine Parsed(byte[] bytes)
    {
        SignedLine? line = SignedLine.Parse(bytes, out string? fault);
        Assert.True(line is not null, $"the line does not parse: {fault}");
        return line;
    }
}
