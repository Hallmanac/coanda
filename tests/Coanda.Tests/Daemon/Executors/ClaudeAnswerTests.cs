using Coanda.Daemon.Executors.ClaudeCode;

namespace Coanda.Tests.Daemon.Executors;

public sealed class ClaudeAnswerTests
{
    [Theory]
    [InlineData("""All done.{"verdict":"done"}""", """{"verdict":"done"}""")]
    [InlineData("All done.\n\n{\"verdict\":\"done\",\"summary\":\"a {brace} in a string\"}\n\n", "{\"verdict\":\"done\",\"summary\":\"a {brace} in a string\"}")]
    [InlineData("Earlier {\"verdict\":\"blocked\"} then later\n{\"verdict\":\"done\",\"nested\":{\"a\":{\"b\":1}}}", "{\"verdict\":\"done\",\"nested\":{\"a\":{\"b\":1}}}")]
    [InlineData("Here it is:\n```json\n{\"verdict\":\"done\"}\n```", "{\"verdict\":\"done\"}")]
    [InlineData("Here it is:\n```\n{\n  \"verdict\": \"done\"\n}\n```\n", "{\n  \"verdict\": \"done\"\n}")]
    public void The_answer_is_the_json_object_that_ends_the_reply_fenced_or_not(string reply, string answer) =>
        ClaudeAnswer.Trailing(reply).Should().Be(answer);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("No document at all.")]
    [InlineData("{\"verdict\":\"done\"}\nThat was my answer.")]
    [InlineData("```json\n{\"verdict\":\"done\"}\n```\nThat was my answer.")]
    [InlineData("A list: [1, 2]")]
    [InlineData("Broken {\"verdict\":")]
    [InlineData("```json\n{\"verdict\":\"done\"}\nnot json\n```")]
    public void A_reply_that_does_not_end_with_a_json_object_has_no_answer(string? reply) =>
        ClaudeAnswer.Trailing(reply).Should().BeNull();
}
