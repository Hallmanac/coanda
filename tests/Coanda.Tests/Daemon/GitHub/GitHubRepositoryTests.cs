using Coanda.Daemon.GitHub;

namespace Coanda.Tests.Daemon.GitHub;

public sealed class GitHubRepositoryTests
{
    [Theory]
    [InlineData("https://github.com/example/repo", "example", "repo")]
    [InlineData("https://github.com/example/repo.git", "example", "repo")]
    [InlineData("git@github.com:example/repo", "example", "repo")]
    [InlineData("git@github.com:example/repo.git", "example", "repo")]
    [InlineData("ssh://git@github.com/example/repo", "example", "repo")]
    [InlineData("ssh://git@github.com/example/repo.git", "example", "repo")]
    [InlineData("https://github.com/Hallmanac/coanda.git", "Hallmanac", "coanda")]
    [InlineData("git@github.com:my-org_2/my.repo-name_3.git", "my-org_2", "my.repo-name_3")]
    [InlineData("https://github.com/example/repo.github", "example", "repo.github")]
    [InlineData("https://github.com/example/repo.git.git", "example", "repo.git")]
    public void A_remote_in_one_of_the_github_forms_parses_to_its_owner_and_repository(string remote, string owner, string name)
    {
        GitHubRepository? parsed = GitHubRepository.Parse(remote);

        Assert.NotNull(parsed);
        Assert.Equal(owner, parsed?.Owner);
        Assert.Equal(name, parsed?.Name);
    }

    [Theory]
    [InlineData("https://example.invalid/repo.git")]
    [InlineData("http://github.com/example/repo")]
    [InlineData("https://www.github.com/example/repo")]
    [InlineData("https://github.example.com/example/repo")]
    [InlineData("https://gitlab.com/example/repo.git")]
    [InlineData("https://user:secret@github.com/example/repo.git")]
    [InlineData("ssh://github.com/example/repo")]
    [InlineData("ssh://deploy@github.com/example/repo")]
    [InlineData("deploy@github.com:example/repo")]
    [InlineData("github.com:example/repo")]
    [InlineData("https://github.com/example")]
    [InlineData("https://github.com/example/repo/")]
    [InlineData("https://github.com/example/repo/extra")]
    [InlineData("https://github.com/example/team/repo.git")]
    [InlineData("https://github.com//repo")]
    [InlineData("https://github.com/example/.git")]
    [InlineData("https://github.com/./repo")]
    [InlineData("https://github.com/../repo")]
    [InlineData("https://github.com/example/..")]
    [InlineData("https://github.com/example/.")]
    [InlineData("https://github.com/example/...git")]
    [InlineData("git@github.com:../repo.git")]
    [InlineData("https://github.com/exa mple/repo")]
    [InlineData("https://github.com/example/re%2Fpo")]
    [InlineData("https://github.com/example/repo?x=1")]
    [InlineData("https://github.com/example/repo#readme")]
    [InlineData(" https://github.com/example/repo")]
    [InlineData("https://github.com/example/repo\n")]
    [InlineData("HTTPS://GITHUB.COM/example/repo")]
    [InlineData("/srv/git/repo.git")]
    [InlineData("")]
    public void A_remote_in_no_github_form_does_not_parse(string remote)
    {
        Assert.Null(GitHubRepository.Parse(remote));
    }

    [Theory]
    [InlineData("https://user:secret@github.com/example/repo.git", "https://github.com/example/repo.git")]
    [InlineData("https://ghp_abc@gitlab.com/a/b", "https://gitlab.com/a/b")]
    [InlineData("ssh://deploy:pw@github.com/example/repo", "ssh://github.com/example/repo")]
    [InlineData("secret@example.com:a/b.git", "example.com:a/b.git")]
    [InlineData("https://a:b/c@github.com/example/repo", "https://github.com/example/repo")]
    [InlineData("https://example.invalid/repo.git", "https://example.invalid/repo.git")]
    [InlineData("https://gitlab.example.com/group/repo@v2.git", "https://gitlab.example.com/group/repo@v2.git")]
    [InlineData("https://gitlab.example.com:8443/group/repo@v2.git", "https://gitlab.example.com:8443/group/repo@v2.git")]
    [InlineData("https://token@gitlab.example.com/group/repo@v2.git", "https://gitlab.example.com/group/repo@v2.git")]
    [InlineData("git@gitlab.example.com:group/repo@v2.git", "gitlab.example.com:group/repo@v2.git")]
    [InlineData("gitlab.example.com:group/repo@v2.git", "gitlab.example.com:group/repo@v2.git")]
    public void Redacting_a_remote_removes_whatever_precedes_the_at_before_its_host(string remote, string redacted)
    {
        Assert.Equal(redacted, GitHubRepository.Redact(remote));
    }
}
