using Coanda.Sdk;
using Coanda.Sdk.Client;
using Coanda.Sdk.Commands;
using Coanda.Sdk.Repositories;

namespace Coanda.Modules.Software.GitHub;

/// <summary>
/// Reads the token the API calls carry in a project, through the credential profile selected for the
/// software module in that project on this node. With none selected, the node's default identity stands:
/// the token is read from <c>gh auth token --hostname github.com</c>, run through the command runner like
/// every other external command, in <paramref name="workingDirectory"/>, the node home. With one selected,
/// the token is read from the file <see cref="SoftwareSettings.GitHubTokenFile"/> in the profile's
/// directory under <paramref name="profilesPath"/>, the module's area of the node home, and from nothing
/// else, so a profile that yields no token never falls back to the default account. The token is returned
/// and never logged or quoted; a reason <c>gh</c> gave none quotes only its error output, and a reason a
/// profile gave none quotes nothing the profile holds.
/// </summary>
public sealed class GitHubToken(ICommandRunner runner, string workingDirectory, string profilesPath)
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

    /// <summary>
    /// The token the profile setting <paramref name="profile"/> yields: the default account's when no
    /// profile is selected, or the selected profile's. It throws <see cref="GitHubProfileTokenException"/>
    /// when a profile is selected, or one this node no longer holds, and it yields no token, and
    /// <see cref="GitHubTokenUnavailableException"/> when the default account yields none.
    /// </summary>
    public async Task<string> ReadAsync(CoreCredentialProfile? profile, CancellationToken cancellationToken) =>
        profile switch
        {
            null or { IsNodeDefault: true } => await ReadDefaultAsync(cancellationToken),
            { IsSelected: true, Profile: { } name } => await ReadProfileAsync(name, cancellationToken),
            { Profile: { } name } => throw new GitHubProfileTokenException(
                name, TokenPathOf(name), $"the credential profile '{name}' selected for the software module is one this node no longer holds."),
            _ => throw new GitHubProfileTokenException(
                null, null, "no credential profile is selected for the software module, and the node's default account does not stand in for one."),
        };

    /// <summary>Where the profile <paramref name="profile"/> keeps the token, or null when the name is not one plain directory name.</summary>
    public string? TokenPathOf(string profile) =>
        RepoLayout.IsPlain(profile)
            ? Path.Combine(profilesPath, profile, SoftwareSettings.GitHubTokenFile)
            : null;

    private async Task<string> ReadProfileAsync(string profile, CancellationToken cancellationToken)
    {
        string path = TokenPathOf(profile)
            ?? throw new GitHubProfileTokenException(profile, null, $"the credential profile '{profile}' is not named by one plain directory name.");
        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception unread) when (unread is IOException or UnauthorizedAccessException)
        {
            // The exception's own message names only the path, never anything the file holds.
            throw new GitHubProfileTokenException(profile, path, $"the credential profile '{profile}' yields no GitHub token: {path} could not be read ({unread.GetType().Name}).");
        }

        string token = text.Trim();
        return token.Length == 0 || token.Any(char.IsWhiteSpace)
            ? throw new GitHubProfileTokenException(profile, path, $"the credential profile '{profile}' yields no GitHub token: {path} holds no single token.")
            : token;
    }

    /// <summary>The default account's token, or throws <see cref="GitHubTokenUnavailableException"/> saying why none is available.</summary>
    private async Task<string> ReadDefaultAsync(CancellationToken cancellationToken)
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

/// <summary>
/// The credential profile selected for the software module yields no GitHub token. The message says why
/// and never carries anything the profile holds; <see cref="Profile"/> is the profile's name, when one
/// is selected, and <see cref="TokenPath"/> the file the token is read from, when the name gives one.
/// </summary>
public sealed class GitHubProfileTokenException(string? profile, string? tokenPath, string message) : Exception(message)
{
    public string? Profile { get; } = profile;

    public string? TokenPath { get; } = tokenPath;
}
