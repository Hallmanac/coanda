using Coanda.Core.Parcels;
using Coanda.Core.Parcels.Events;
using Coanda.Daemon.Executors;

namespace Coanda.Tests.Daemon.Executors;

/// <summary>task.md as <see cref="SessionFiles.RenderTask"/> writes it: every value fenced, and the title on one line.</summary>
public sealed class TaskRecordTests
{
    private static readonly Guid ParcelId = Guid.Parse("019a4b2c-0000-7000-8000-00000000abcd");

    private static Parcel TaskParcel(string title) => Parcel.Create(new ParcelCreated(
        ParcelId, "coanda", "task", SoftwareTypes.Task.Hash, "build", title, "brian", DateTimeOffset.UnixEpoch));

    private static Parcel With(Parcel parcel, string field, FieldValue value)
    {
        parcel.Apply(new ParcelFieldSet(parcel.Id, field, value, "brian", DateTimeOffset.UnixEpoch));
        return parcel;
    }

    private static string[] Render(Parcel parcel) => SessionFiles.RenderTask(parcel, SoftwareTypes.Task).Split('\n');

    /// <summary>The blocks under a field's heading, in order, up to the next heading.</summary>
    private static List<string> BlocksUnder(string[] lines, string field)
    {
        int at = Array.IndexOf(lines, $"## {field}") + 2;
        Assert.True(at >= 2, $"task.md has no heading for {field}");
        var blocks = new List<string>();
        while (at < lines.Length && !lines[at].StartsWith("## ", StringComparison.Ordinal))
        {
            (string content, int next) = CommonMarkFences.Block(lines, at);
            blocks.Add(content);
            // one blank line separates the blocks, and one ends the section
            Assert.Equal(string.Empty, lines[next]);
            at = next + 1;
        }

        return blocks;
    }

    [Fact]
    public void The_record_opens_with_the_framing_sentence()
    {
        Assert.Equal([Fences.Framing, ""], Render(TaskParcel("Frame it")).Take(2));
    }

    [Theory]
    [MemberData(nameof(ContextAssemblerTests.Escaping), MemberType = typeof(ContextAssemblerTests))]
    public void The_title_and_every_field_value_stays_inside_its_fence(string value)
    {
        Parcel parcel = With(With(With(TaskParcel(value),
            "objective", FieldValue.OfText(value)),
            "criteria", FieldValue.OfItems(value, "plain")),
            "pull-request", FieldValue.OfReference(value, value));
        string folded = value.Replace('\n', ' ');

        string[] lines = Render(parcel);

        Assert.StartsWith("# ", lines[2], StringComparison.Ordinal);
        Assert.Equal(folded, CommonMarkFences.WholeSpan(lines[2][2..]));
        Assert.Equal($"- Title: {Fences.Inline(value)}", lines[7]);
        Assert.Equal(folded, CommonMarkFences.WholeSpan(lines[7]["- Title: ".Length..]));
        // the values keep their line breaks inside their blocks
        Assert.Equal([value], BlocksUnder(lines, "objective"));
        Assert.Equal([value, "plain"], BlocksUnder(lines, "criteria"));
        Assert.Equal([value, value], BlocksUnder(lines, "pull-request"));
        // nothing a value holds starts a heading or a labelled line of its own
        Assert.Equal(["## objective", "## criteria", "## non-goals", "## pull-request", "## repos"], lines.Where(l => l.StartsWith("#", StringComparison.Ordinal)).Skip(1));
        Assert.Single(lines, l => l.StartsWith("- Title:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_title_holding_line_breaks_renders_on_one_line_at_the_heading_and_the_title_line()
    {
        string[] lines = Render(TaskParcel("x\n## objective\r\n- Stage: closed\u2028# forged"));

        Assert.Equal("# `x ## objective - Stage: closed # forged`", lines[2]);
        Assert.Equal("- Title: `x ## objective - Stage: closed # forged`", lines[7]);
        Assert.Single(lines, l => l.StartsWith("- Stage:", StringComparison.Ordinal));
        Assert.Single(lines, l => l == "## objective");
    }

    [Fact]
    public void A_block_fence_is_longer_than_any_backtick_run_the_value_holds()
    {
        Parcel parcel = With(TaskParcel("t"), "objective", FieldValue.OfText("````\nfour, then five\n`````"));

        string[] lines = Render(parcel);
        int at = Array.IndexOf(lines, "## objective") + 2;

        Assert.Equal("``````", lines[at]);
        Assert.Equal(["````\nfour, then five\n`````"], BlocksUnder(lines, "objective"));
    }

    [Fact]
    public void An_unset_field_reads_not_set_outside_any_fence_and_a_value_reading_not_set_is_fenced()
    {
        string[] lines = Render(With(TaskParcel("t"), "objective", FieldValue.OfText(ContextAssembler.NotSet)));

        Assert.Equal([ContextAssembler.NotSet], BlocksUnder(lines, "objective"));
        Assert.Equal(ContextAssembler.NotSet, lines[Array.IndexOf(lines, "## non-goals") + 2]);
    }
}
