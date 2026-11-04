using Coanda.Daemon.Executors.ClaudeCode;

namespace Coanda.Tests.Daemon.Executors;

public sealed class ClaudeStreamTests
{
    [Fact]
    public void An_assistant_usage_figure_that_is_not_a_number_reads_as_zero()
    {
        ClaudeStream stream = new();

        stream.Read("""{"type":"assistant","message":{"id":"m1","usage":{"input_tokens":10,"output_tokens":5,"cache_creation_input_tokens":null,"cache_read_input_tokens":"7"}}}""");

        stream.MessageCount.Should().Be(1);
        stream.MessageTokens.Should().Be(15);
    }

    [Fact]
    public void A_result_whose_turns_and_duration_are_not_numbers_is_still_read()
    {
        ClaudeStream stream = new();

        stream.Read("""{"type":"result","subtype":"success","is_error":false,"result":"done","total_cost_usd":0.5,"num_turns":null,"duration_ms":"12","usage":{"input_tokens":3,"output_tokens":null},"model_usage_since_last_result":{"m":{"inputTokens":null,"outputTokens":4,"costUSD":0.25}}}""");

        ClaudeResult? result = stream.Result;
        result.Should().NotBeNull();
        result.Text.Should().Be("done");
        result.Turns.Should().Be(0);
        result.Duration.Should().Be(TimeSpan.Zero);
        result.UsageTokens.Should().Be(3);
        result.SinceLastResult.Should().Be((4L, 0.25m));
    }

    [Fact]
    public void A_result_with_out_of_range_turns_and_duration_is_clamped_rather_than_dropped()
    {
        ClaudeStream stream = new();

        stream.Read("""{"type":"result","subtype":"success","is_error":false,"result":"done","num_turns":99999999999,"duration_ms":-5}""");

        ClaudeResult? result = stream.Result;
        result.Should().NotBeNull();
        result.Turns.Should().Be(int.MaxValue);
        result.Duration.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void An_event_whose_figures_overflow_when_summed_is_passed_over()
    {
        ClaudeStream stream = new();

        stream.Read("""{"type":"assistant","message":{"id":"m1","usage":{"input_tokens":9223372036854775807,"output_tokens":1}}}""");

        stream.MessageCount.Should().Be(0);
    }
}
