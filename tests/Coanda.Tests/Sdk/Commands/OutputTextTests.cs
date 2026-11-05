using Coanda.Sdk.Commands;

namespace Coanda.Tests.Sdk.Commands;

public sealed class OutputTextTests
{
    [Theory]
    [InlineData("https://user:secret@github.com/you/repo.git", "https://github.com/you/repo.git")]
    [InlineData("fetching ssh://git@github.com/you/repo.git now", "fetching ssh://github.com/you/repo.git now")]
    [InlineData("https://x-access-token:p@ss@host/a and http://u@h/b", "https://host/a and http://h/b")]
    [InlineData("git@github.com:you/repo.git", "git@github.com:you/repo.git")]
    [InlineData("https://github.com/you/repo.git, mail me@example.com", "https://github.com/you/repo.git, mail me@example.com")]
    public void Redaction_removes_the_user_information_of_urls_with_a_scheme_and_nothing_else(string text, string redacted)
    {
        Assert.Equal(redacted, OutputText.Redact(text));
    }

    [Fact]
    public void The_last_lines_drop_a_trailing_line_break_and_make_line_endings_plain()
    {
        Assert.Equal(["b", "c"], OutputText.LastLines("a\r\nb\r\nc\r\n", 2));
        Assert.Equal(["a", "b"], OutputText.LastLines("a\nb", 5));
        Assert.Empty(OutputText.LastLines(string.Empty, 5));
    }

    [Fact]
    public async Task The_runner_used_when_none_is_supplied_starts_nothing_and_fails_every_command()
    {
        var command = new CommandLine("git", ["status"], Path.GetTempPath(), CommandLine.NoVariables);

        CommandNotStartedException exception = await Assert.ThrowsAsync<CommandNotStartedException>(() => new NoCommandRunner().RunAsync(command, CancellationToken.None));
        Assert.StartsWith("git could not be started: this daemon was started with no command runner", exception.Message, StringComparison.Ordinal);
    }
}
