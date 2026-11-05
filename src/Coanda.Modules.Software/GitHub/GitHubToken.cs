using Coanda.Sdk;
using Coanda.Sdk.Commands;

namespace Coanda.Modules.Software.GitHub;

/// <summary>
/// Reads the token the API calls carry from <c>gh auth token --hostname github.com</c>, run through
/// the command runner like every other external command, in <paramref name="workingDirectory"/>, the
/// node home. The token is returned and never logged or quoted; a reason <c>gh</c> gave none quotes only
/// its error output.
/// </summary>
public sealed class GitHubToken(ICommandRunner runner, string workingDirectory)
{
    /// <summary>How long <c>gh</c> may run before it is ended and no token is read.</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(1);

    /// <summary>How many lines of <c>gh</c>'s error output a reason quotes.</summary>
    private const int QuotedLines = 5;

    /// <summary>The variables <c>gh</c> is given, so it never waits on a prompt.</summary>
    private static readonly IReadOnlyDictionary<string, string> Environment = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["GH_PROMPT_DISABLED"] = "1",
        ["NO_COLOR"] = "1",
    };

    /// <summary>The command that prints the token.</summary>
    public CommandLine Command => new("gh", ["auth", "token", "--hostname", GitHubClient.Host], workingDirectory, Environment) { Limit = Limit };

    /// <summary>The token, or throws <see cref="GitHubTokenUnavailableException"/> saying why none is available.</summary>
    public async Task<string> ReadAsync(CancellationToken cancellationToken)
    {
        CommandResult result;
        try
        {
            result = await runner.RunAsync(Command, cancellationToken);
        }
        catch (CommandNotStartedException unstarted)
        {
            throw new GitHubTokenUnavailableException(unstarted.Message);
        }

        if (!result.Succeeded)
        {
            IReadOnlyList<string> lines = OutputText.LastLines(result.StandardError, QuotedLines);
            string ended = result.TimedOut
                ? $"was still running after {Durations.Describe(Limit)} and was ended"
                : $"exited with code {result.ExitCode}";
            string quoted = lines.Count == 0
                ? "it wrote no error output."
                : $"its error output ends:\n{string.Join('\n', lines)}";
            throw new GitHubTokenUnavailableException(OutputText.Redact($"gh auth token --hostname {GitHubClient.Host} {ended}; {quoted}"));
        }

        string token = result.StandardOutput.Trim();
        return token.Length == 0 || token.Any(char.IsWhiteSpace)
            ? throw new GitHubTokenUnavailableException($"gh auth token --hostname {GitHubClient.Host} printed no single token.")
            : token;
    }
}

/// <summary>No GitHub token could be read; the message says why and never carries what <c>gh</c> printed on its standard output.</summary>
public sealed class GitHubTokenUnavailableException(string message) : Exception(message);
