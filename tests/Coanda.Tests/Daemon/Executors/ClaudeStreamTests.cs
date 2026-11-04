using Coanda.Daemon.Executors.ClaudeCode;

namespace Coanda.Tests.Daemon.Executors;

public sealed class ClaudeStreamTests
{
    [Fact]
    public void An_assistant_usage_figure_that_is_not_a_number_reads_as_zero()
    {
        ClaudeStream stream = new();

        stream.Read("""{"type":"assistant","message":{"id":"m1","usage":{"input_tokens":10,"output_tokens":5,"cache_creation_input_tokens":null,"cache_read_input_tokens":"7"}}}""");

        Assert.Equal(1, stream.MessageCount);
        Assert.Equal(15, stream.MessageTokens);
    }

    [Fact]
    public void A_result_whose_turns_and_duration_are_not_numbers_is_still_read()
    {
        ClaudeStream stream = new();

        stream.Read("""{"type":"result","subtype":"success","is_error":false,"result":"done","total_cost_usd":0.5,"num_turns":null,"duration_ms":"12","usage":{"input_tokens":3,"output_tokens":null},"model_usage_since_last_result":{"m":{"inputTokens":null,"outputTokens":4,"costUSD":0.25}}}""");

        ClaudeResult? result = stream.Result;
        Assert.NotNull(result);
        Assert.Equal("done", result.Text);
        Assert.Equal(0, result.Turns);
        Assert.Equal(TimeSpan.Zero, result.Duration);
        Assert.Equal(3, result.UsageTokens);
        Assert.Equal((4L, 0.25m), result.SinceLastResult);
    }

    [Fact]
    public void A_result_with_out_of_range_turns_and_duration_is_clamped_rather_than_dropped()
    {
        ClaudeStream stream = new();

        stream.Read("""{"type":"result","subtype":"success","is_error":false,"result":"done","num_turns":99999999999,"duration_ms":-5}""");

        ClaudeResult? result = stream.Result;
        Assert.NotNull(result);
        Assert.Equal(int.MaxValue, result.Turns);
        Assert.Equal(TimeSpan.Zero, result.Duration);
    }

    [Fact]
    public void An_event_whose_figures_overflow_when_summed_is_passed_over()
    {
        ClaudeStream stream = new();

        stream.Read("""{"type":"assistant","message":{"id":"m1","usage":{"input_tokens":9223372036854775807,"output_tokens":1}}}""");

        Assert.Equal(0, stream.MessageCount);
    }

    private const string Init = """{"type":"system","subtype":"init","session_id":"s","claude_code_version":"2.1.318"}""";

    [Fact]
    public void The_settings_document_reads_as_applied_only_once_its_evidence_comes_before_the_init_event()
    {
        ClaudeStream stream = new();
        // the stream holds neither yet
        Assert.Null(stream.SettingsApplied);

        stream.Read(Hook("SessionStart", "SessionStart:startup", $"{SettingsEvidence.Marker}\\n"));
        stream.Read(Init);

        Assert.True(stream.SettingsApplied);
        Assert.True(stream.Initialized);
        Assert.Equal("2.1.318", stream.CliVersion);
    }

    [Fact]
    public void An_init_event_without_the_evidence_before_it_reads_as_not_applied_whatever_comes_after()
    {
        ClaudeStream stream = new();

        stream.Read(Init);
        stream.Read(Hook("SessionStart", "SessionStart:startup", $"{SettingsEvidence.Marker}\\n"));

        Assert.False(stream.SettingsApplied);
    }

    [Theory]
    [InlineData("SessionStart", "", "nothing on its standard error")]
    [InlineData("SessionStart", "another hook ran", "another hook's output")]
    [InlineData("SessionStart", "coanda-settings-document-applied-not", "a line that only begins with the marker")]
    [InlineData("PreToolUse", "coanda-settings-document-applied", "a hook of another event")]
    public void A_hook_response_that_does_not_quote_the_marker_from_a_session_start_hook_is_not_evidence(string hookEvent, string stderr, string what)
    {
        ClaudeStream stream = new();

        stream.Read(Hook(hookEvent, $"{hookEvent}:startup", stderr));
        stream.Read(Init);

        Assert.False(stream.SettingsApplied, what);
    }

    [Fact]
    public void The_marker_on_a_hooks_standard_output_alone_is_not_evidence()
    {
        ClaudeStream stream = new();

        stream.Read($$"""{"type":"system","subtype":"hook_response","hook_name":"SessionStart:startup","hook_event":"SessionStart","output":"{{SettingsEvidence.Marker}}\n","stdout":"{{SettingsEvidence.Marker}}\n","stderr":"","exit_code":0}""");
        stream.Read(Init);

        Assert.False(stream.SettingsApplied, "standard output reaches the session's context, so the evidence is read from standard error alone");
    }

    [Fact]
    public void The_marker_among_other_lines_of_a_session_start_hooks_standard_error_is_evidence()
    {
        ClaudeStream stream = new();

        stream.Read(Hook("SessionStart", "SessionStart:resume", $"warming up\\r\\n{SettingsEvidence.Marker}\\r\\n"));

        Assert.True(stream.SettingsApplied);
        Assert.False(stream.Initialized);
    }

    private static string Hook(string hookEvent, string name, string stderr) =>
        $$"""{"type":"system","subtype":"hook_response","hook_name":"{{name}}","hook_event":"{{hookEvent}}","output":"{{stderr}}","stdout":"","stderr":"{{stderr}}","exit_code":0}""";
}
