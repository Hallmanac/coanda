using System.Text;
using Coanda.Daemon.Transport;

namespace Coanda.Tests.Daemon.Transport;

/// <summary>
/// Verifying a run of lines from one origin in one project, with and without a held position: a
/// valid run verifies in full, each fault names the first bad line and keeps the lines before it, and
/// a run ending before the held sequence is a rewind.
/// </summary>
public sealed class ChainVerifierTests
{
    [Fact]
    public void A_valid_run_from_the_origins_first_line_is_verified_in_full()
    {
        List<byte[]> run = Lines.Run(4);

        ChainVerification result = Verify(run);

        AssertVerified(result, run, 4);
    }

    [Fact]
    public void An_empty_run_verifies_with_no_line()
    {
        ChainVerification result = Verify([], new HeldPosition(2, SignedLine.HashOf(Lines.Run(2)[1])));

        Assert.Equal(ChainOutcome.Verified, result.Outcome);
        Assert.Equal(0, result.VerifiedLines);
        Assert.Null(result.VerifiedThrough);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void With_a_held_position_a_run_may_start_at_any_sequence_up_to_the_one_after_it(int start)
    {
        List<byte[]> chain = Lines.Run(5);
        var held = new HeldPosition(3, SignedLine.HashOf(chain[2]));
        List<byte[]> run = chain[(start - 1)..];

        ChainVerification result = Verify(run, held);

        AssertVerified(result, chain, 5);
        Assert.Equal(run.Count, result.VerifiedLines);
    }

    [Fact]
    public void A_line_that_does_not_parse_is_named_by_the_sequence_expected_at_its_place()
    {
        List<byte[]> run = Lines.Run(4);
        run[2] = Encoding.UTF8.GetBytes(Lines.Text(run[2])[..^5]);

        ChainVerification result = Verify(run);

        AssertBroken(result, run, ChainFaultKind.Unparsable, sequence: 3, index: 2);
    }

    [Fact]
    public void A_line_whose_signature_does_not_verify_breaks_the_run()
    {
        List<byte[]> run = Lines.Run(4);
        run[1] = Lines.Replace(run[1], "{\"n\":2}", "{\"n\":7}");

        ChainVerification result = Verify(run);

        AssertBroken(result, run, ChainFaultKind.BadSignature, sequence: 2, index: 1);
    }

    [Fact]
    public void A_line_signed_by_a_key_the_reader_does_not_accept_breaks_the_run()
    {
        List<byte[]> run = Lines.Run(3, Lines.OtherKey);

        ChainVerification result = Verify(run);

        AssertBroken(result, run, ChainFaultKind.UnacceptedSigner, sequence: 1, index: 0);
        Assert.Contains(Lines.OtherKey.PublicKey, result.Fault?.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_from_another_origin_breaks_the_run()
    {
        List<byte[]> run = Lines.Run(3);
        run[2] = Lines.Resign(run[2], Lines.Origin.ToString("D"), Guid.Parse("0196f3a0-0000-7000-8000-0000000000cc").ToString("D"));

        ChainVerification result = Verify(run);

        AssertBroken(result, run, ChainFaultKind.WrongOrigin, sequence: 3, index: 2);
    }

    [Fact]
    public void A_line_from_another_project_breaks_the_run()
    {
        List<byte[]> run = Lines.Run(3);
        run[1] = Lines.Resign(run[1], Lines.Project.ToString("D"), Guid.Parse("0196f3a0-0000-7000-8000-000000000002").ToString("D"));

        ChainVerification result = Verify(run);

        AssertBroken(result, run, ChainFaultKind.WrongProject, sequence: 2, index: 1);
    }

    [Fact]
    public void A_gap_breaks_the_run_at_the_line_past_it()
    {
        List<byte[]> chain = Lines.Run(5);
        List<byte[]> run = [chain[0], chain[1], chain[3], chain[4]];

        ChainVerification result = Verify(run);

        AssertBroken(result, run, ChainFaultKind.WrongSequence, sequence: 4, index: 2);
        Assert.Contains("gap", result.Fault?.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_repeated_line_breaks_the_run_at_the_repeat()
    {
        List<byte[]> chain = Lines.Run(3);
        List<byte[]> run = [chain[0], chain[1], chain[1], chain[2]];

        ChainVerification result = Verify(run);

        AssertBroken(result, run, ChainFaultKind.WrongSequence, sequence: 2, index: 2);
        Assert.Contains("repeats", result.Fault?.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_step_back_breaks_the_run_at_the_earlier_line()
    {
        List<byte[]> chain = Lines.Run(3);
        List<byte[]> run = [chain[0], chain[1], chain[2], chain[0]];

        ChainVerification result = Verify(run);

        AssertBroken(result, run, ChainFaultKind.WrongSequence, sequence: 1, index: 3);
        Assert.Contains("steps back", result.Fault?.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_a_held_position_a_run_starting_past_the_origins_first_line_breaks_at_once()
    {
        List<byte[]> run = Lines.Run(4)[1..];

        ChainVerification result = Verify(run);

        AssertBroken(result, run, ChainFaultKind.WrongSequence, sequence: 2, index: 0);
    }

    [Fact]
    public void With_a_held_position_a_run_starting_past_the_one_after_it_breaks_at_once()
    {
        List<byte[]> chain = Lines.Run(6);
        List<byte[]> run = chain[4..];

        ChainVerification result = Verify(run, new HeldPosition(3, SignedLine.HashOf(chain[2])));

        AssertBroken(result, run, ChainFaultKind.WrongSequence, sequence: 5, index: 0);
    }

    [Fact]
    public void A_previous_hash_that_is_not_the_line_befores_breaks_the_run()
    {
        List<byte[]> run = Lines.Run(3);
        string hash = SignedLine.HashOf(run[0]);
        run[1] = Lines.Resign(run[1], hash, SignedLine.HashOf(run[2]));

        ChainVerification result = Verify(run);

        AssertBroken(result, run, ChainFaultKind.WrongPreviousHash, sequence: 2, index: 1);
    }

    [Fact]
    public void An_origins_first_line_naming_a_previous_hash_breaks_the_run()
    {
        List<byte[]> run = Lines.Run(2);
        run[0] = Lines.Resign(run[0], "\"previous\":null", $"\"previous\":\"{SignedLine.HashOf(run[1])}\"");

        ChainVerification result = Verify(run);

        AssertBroken(result, run, ChainFaultKind.WrongPreviousHash, sequence: 1, index: 0);
    }

    [Fact]
    public void A_later_line_naming_no_previous_hash_breaks_the_run()
    {
        List<byte[]> run = Lines.Run(2);
        run[1] = Lines.Resign(run[1], $"\"previous\":\"{SignedLine.HashOf(run[0])}\"", "\"previous\":null");

        ChainVerification result = Verify(run);

        AssertBroken(result, run, ChainFaultKind.WrongPreviousHash, sequence: 2, index: 1);
    }

    [Fact]
    public void A_run_starting_after_the_held_line_must_name_the_held_hash_as_its_first_lines_previous_hash()
    {
        List<byte[]> chain = Lines.Run(5);
        List<byte[]> run = chain[3..];

        ChainVerification result = Verify(run, new HeldPosition(3, SignedLine.HashOf(chain[0])));

        AssertBroken(result, run, ChainFaultKind.WrongPreviousHash, sequence: 4, index: 0);
    }

    [Fact]
    public void A_line_at_the_held_sequence_with_another_hash_breaks_the_run_and_no_line_is_verified()
    {
        List<byte[]> chain = Lines.Run(5);
        var held = new HeldPosition(3, SignedLine.HashOf(chain[0]));

        ChainVerification result = Verify(chain, held);

        Assert.Equal(ChainOutcome.Broken, result.Outcome);
        Assert.Equal(new ChainFault(3, 2, ChainFaultKind.HeldHashMismatch, result.Fault?.Reason ?? ""), result.Fault);
        AssertNoneVerified(result);
    }

    [Fact]
    public void Any_fault_at_the_held_sequence_leaves_no_line_verified()
    {
        List<byte[]> chain = Lines.Run(5);
        var held = new HeldPosition(3, SignedLine.HashOf(chain[2]));
        List<byte[]> run = [chain[0], chain[1], Lines.Replace(chain[2], "{\"n\":3}", "{\"n\":9}"), chain[3]];

        ChainVerification result = Verify(run, held);

        Assert.Equal(ChainOutcome.Broken, result.Outcome);
        Assert.Equal(ChainFaultKind.BadSignature, result.Fault?.Kind);
        Assert.Equal(3, result.Fault?.Sequence);
        AssertNoneVerified(result);
    }

    [Fact]
    public void A_fault_before_the_held_sequence_keeps_the_lines_before_it_verified()
    {
        List<byte[]> chain = Lines.Run(5);
        var held = new HeldPosition(4, SignedLine.HashOf(chain[3]));
        List<byte[]> run = [chain[0], chain[1], Lines.Replace(chain[2], "{\"n\":3}", "{\"n\":9}"), chain[3]];

        ChainVerification result = Verify(run, held);

        AssertBroken(result, run, ChainFaultKind.BadSignature, sequence: 3, index: 2);
    }

    [Fact]
    public void A_run_that_ends_before_the_held_sequence_is_a_rewind()
    {
        List<byte[]> chain = Lines.Run(5);
        var held = new HeldPosition(5, SignedLine.HashOf(chain[4]));
        List<byte[]> run = chain[..3];

        ChainVerification result = Verify(run, held);

        Assert.Equal(ChainOutcome.Rewind, result.Outcome);
        Assert.Null(result.Fault);
        Assert.Equal(3, result.VerifiedThrough);
    }

    private static ChainVerification Verify(IReadOnlyList<byte[]> run, HeldPosition? held = null) =>
        ChainVerifier.Verify(run, Lines.Project, Lines.Origin, Lines.Accepted, held);

    private static void AssertVerified(ChainVerification result, List<byte[]> chain, long through)
    {
        Assert.Equal(ChainOutcome.Verified, result.Outcome);
        Assert.Null(result.Fault);
        Assert.Equal(through, result.VerifiedThrough);
        Assert.Equal(SignedLine.HashOf(chain[(int)through - 1]), result.VerifiedThroughHash);
    }

    /// <summary>The run broke at <paramref name="index"/>, and every line before it is reported verified.</summary>
    private static void AssertBroken(ChainVerification result, List<byte[]> run, ChainFaultKind kind, long sequence, int index)
    {
        Assert.Equal(ChainOutcome.Broken, result.Outcome);
        ChainFault fault = Assert.IsType<ChainFault>(result.Fault);
        Assert.Equal(kind, fault.Kind);
        Assert.Equal(sequence, fault.Sequence);
        Assert.Equal(index, fault.Index);
        Assert.False(string.IsNullOrWhiteSpace(fault.Reason));
        Assert.Equal(index, result.VerifiedLines);
        if (index == 0)
        {
            AssertNoneVerified(result);
        }
        else
        {
            Assert.Equal(SignedLine.HashOf(run[index - 1]), result.VerifiedThroughHash);
            Assert.Equal(SignedLine.Parse(run[index - 1], out _)?.Content.Sequence, result.VerifiedThrough);
        }
    }

    private static void AssertNoneVerified(ChainVerification result)
    {
        Assert.Equal(0, result.VerifiedLines);
        Assert.Null(result.VerifiedThrough);
        Assert.Null(result.VerifiedThroughHash);
    }
}
