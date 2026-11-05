namespace Coanda.Modules.Software.GitHub;

/// <summary>An open pull request on GitHub: its number in its repository and its web URL.</summary>
public sealed record GitHubPullRequest(int Number, string Url);
