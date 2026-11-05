using Coanda.Core.Projects;

namespace Coanda.Tests.Core;

/// <summary>A repo's name, as <see cref="ProjectRepo.NameOf"/> derives it from the remote.</summary>
public sealed class ProjectRepoTests
{
    [Theory]
    [InlineData("https://github.com/Hallmanac/coanda.git", "coanda")]
    [InlineData("https://github.com/Hallmanac/coanda", "coanda")]
    [InlineData("https://github.com/Hallmanac/coanda/", "coanda")]
    [InlineData("git@github.com:Hallmanac/Coanda.git", "coanda")]
    [InlineData("ssh://git@github.com/Hallmanac/coanda.git/", "coanda")]
    [InlineData("git@example.com:coanda", "coanda")]
    [InlineData("/srv/git/my repo.git", "my-repo")]
    [InlineData(@"C:\work\repos\Tool_Kit.GIT", "tool_kit")]
    [InlineData("https://example.com/a/we!rd%20na*me?.git", "we-rd-20na-me")]
    [InlineData("https://example.com/a/.hidden.", "hidden")]
    [InlineData("https://example.com/a/v1.2", "v1.2")]
    [InlineData("  https://example.com/a/spaced.git  ", "spaced")]
    public void A_repo_is_named_after_its_remote_s_last_path_segment(string remote, string name)
    {
        Assert.Equal(name, ProjectRepo.NameOf(remote));
    }

    [Theory]
    [InlineData("https://example.com/a/---")]
    [InlineData("https://example.com/a/-.git")]
    [InlineData("https://example.com/a/...")]
    [InlineData("https://example.com/a/***")]
    [InlineData("")]
    public void A_remote_that_leaves_nothing_to_name_names_the_fallback(string remote)
    {
        Assert.Equal(ProjectRepo.Fallback, ProjectRepo.NameOf(remote));
    }

    [Theory]
    [InlineData("https://example.com/a/CON.git", "con-repo")]
    [InlineData("https://example.com/a/nul", "nul-repo")]
    [InlineData("https://example.com/a/com1.tools", "com1-repo.tools")]
    [InlineData("https://example.com/a/lpt9", "lpt9-repo")]
    [InlineData("https://example.com/a/console", "console")]
    public void A_name_windows_reserves_for_a_device_gains_a_suffix_before_its_dot(string remote, string name)
    {
        Assert.Equal(name, ProjectRepo.NameOf(remote));
    }

    [Fact]
    public void A_name_is_cut_to_the_longest_a_name_may_be()
    {
        string name = ProjectRepo.NameOf($"https://example.com/a/{new string('x', 300)}");

        Assert.Equal(ProjectRepo.MaxNameLength, name.Length);
    }

    [Theory]
    [InlineData("https://github.com/Hallmanac/coanda.git")]
    [InlineData("git@example.com:x/a b:c*d?e|f<g>h\"i.git")]
    [InlineData(@"C:\work\..\..")]
    [InlineData("https://example.com/a/AUX.txt")]
    [InlineData("https://example.com/a/trailing. ")]
    public void Every_name_is_one_plain_directory_name_on_every_platform(string remote)
    {
        string name = ProjectRepo.NameOf(remote);

        Assert.NotEmpty(name);
        Assert.DoesNotContain(name, n => n is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' or ' ');
        Assert.NotEqual(".", name);
        Assert.NotEqual("..", name);
        Assert.False(name.EndsWith('.'));
        Assert.Matches("^[a-z0-9._-]+$", name);
    }
}
