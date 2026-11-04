using Coanda.Daemon.Commands;

namespace Coanda.Tests.Daemon.Commands;

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
        OutputText.Redact(text).Should().Be(redacted);
    }

    [Fact]
    public void The_last_lines_drop_a_trailing_line_break_and_make_line_endings_plain()
    {
        OutputText.LastLines("a\r\nb\r\nc\r\n", 2).Should().Equal("b", "c");
        OutputText.LastLines("a\nb", 5).Should().Equal("a", "b");
        OutputText.LastLines(string.Empty, 5).Should().BeEmpty();
    }

    [Fact]
    public async Task The_runner_used_when_none_is_supplied_starts_nothing_and_fails_every_command()
    {
        var command = new CommandLine("git", ["status"], Path.GetTempPath(), CommandLine.NoVariables);

        await FluentActions.Awaiting(() => new NoCommandRunner().RunAsync(command, CancellationToken.None))
            .Should().ThrowAsync<CommandNotStartedException>().WithMessage("git could not be started: this daemon was started with no command runner*");
    }
}
