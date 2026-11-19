namespace Coanda.Tests.Vocabulary;

/// <summary>
/// The vocabulary test's matcher over source written here: it reports a listed noun in a name the source
/// declares and in a literal's text, and nothing in a comment, in a name it only uses, or inside a longer word.
/// </summary>
public sealed class DomainNounMatcherTests
{
    private const string File = "src/Sample.cs";

    [Fact]
    public void It_reports_a_noun_in_a_declared_type_name()
    {
        NounHit hit = Assert.Single(Hits("namespace Sample;\n\npublic sealed class TaskQueue;\n"));

        Assert.Equal(new NounHit(File, 3, "task", NounSite.DeclaredName, "TaskQueue"), hit);
    }

    [Fact]
    public void It_reports_a_noun_in_a_declared_local()
    {
        NounHit hit = Assert.Single(Hits("static class C\n{\n    static void M()\n    {\n        int reviewCount = 0;\n        _ = reviewCount;\n    }\n}\n"));

        Assert.Equal(new NounHit(File, 5, "review", NounSite.DeclaredName, "reviewCount"), hit);
    }

    [Fact]
    public void It_reports_a_noun_in_a_string_literal()
    {
        NounHit hit = Assert.Single(Hits("static class C\n{\n    const string Said = \"open an issue first\";\n}\n"));

        Assert.Equal(new NounHit(File, 3, "issue", NounSite.Literal, "open an issue first"), hit);
    }

    [Fact]
    public void It_reports_a_noun_in_a_camel_case_string_literal()
    {
        NounHit hit = Assert.Single(Hits("static class C\n{\n    const string Member = \"primaryBranch\";\n}\n"));

        Assert.Equal(new NounHit(File, 3, "branch", NounSite.Literal, "primaryBranch"), hit);
    }

    [Fact]
    public void It_reports_a_noun_in_an_interpolated_and_a_raw_string()
    {
        IReadOnlyList<NounHit> hits = Hits("static class C\n{\n    static string M(int n) => $\"{n} decisions\" + \"\"\"\n        an epic\n        \"\"\";\n}\n");

        Assert.Equal(
            [("decision", 3, " decisions"), ("epic", 4, "an epic")],
            hits.Select(hit => (hit.Noun, hit.Line, hit.Text)));
    }

    [Fact]
    public void A_hit_in_a_multi_line_literal_names_the_line_of_the_whole_word_not_a_longer_word_holding_it()
    {
        NounHit hit = Assert.Single(Hits("static class C\n{\n    const string Said = \"\"\"\n        the latest\n        test run\n        \"\"\";\n}\n"));

        Assert.Equal(("test", 5), (hit.Noun, hit.Line));
    }

    [Fact]
    public void A_hit_after_an_escape_sequence_names_the_line_the_literal_is_on()
    {
        NounHit hit = Assert.Single(Hits("static class C\n{\n    const string Said = \"one\\ntest\";\n}\n"));

        Assert.Equal(("test", 3), (hit.Noun, hit.Line));
    }

    [Theory]
    [InlineData("sealed class PullRequestView;", "PullRequestView")]
    [InlineData("sealed class OpenPullRequests;", "OpenPullRequests")]
    [InlineData("static class C { static void M(int pull_request) { } }", "pull_request")]
    public void It_reports_pull_request_in_a_name(string source, string name) =>
        Assert.Equal([("pull request", name)], Hits(source).Select(hit => (hit.Noun, hit.Text)));

    [Theory]
    [InlineData("pull-request")]
    [InlineData("pullRequest")]
    [InlineData("open the pull requests")]
    public void It_reports_pull_request_in_a_literal(string literal) =>
        Assert.Equal([("pull request", literal)], Hits($"static class C {{ const string S = \"{literal}\"; }}").Select(hit => (hit.Noun, hit.Text)));

    [Theory]
    [InlineData("sealed class GitHubClient;", "GitHubClient")]
    [InlineData("sealed class GithubClient;", "GithubClient")]
    public void It_reports_github_in_a_name(string source, string name) =>
        Assert.Equal([("GitHub", name)], Hits(source).Select(hit => (hit.Noun, hit.Text)));

    [Theory]
    [InlineData("git@github.com:you/coanda.git")]
    [InlineData("GitHub")]
    [InlineData("git-hub")]
    public void It_reports_github_in_a_literal(string literal) =>
        Assert.Equal([("GitHub", literal)], Hits($"static class C {{ const string S = \"{literal}\"; }}").Select(hit => (hit.Noun, hit.Text)));

    [Fact]
    public void It_reports_the_names_a_file_declares_of_every_kind()
    {
        const string source = """
            namespace Tasks.Here;

            using IdeaAlias = System.String;

            enum Kind { Epic }

            interface ICard<TLesson> { }

            delegate void OnMerge();

            sealed record Store(int Rebase)
            {
                event System.Action? Diffs;
                int Fixes { get; }
                System.Func<int, int> F = worktree => worktree;

                void Run(object value)
                {
                    if (value is int evaluation) { }
                    int.TryParse("1", out int builds);
                    foreach (int test in new int[0]) { }
                    using System.IO.MemoryStream branch = new();
                    try { } catch (System.Exception issue) { }
                    (int reviews, int other) pair = (1, 2);
                    var named = (discovery: 1, other: 2);
                    var anonymous = new { Decision = 1 };
                    void Fix() { }
                    lesson: ;
                    _ = from task in new int[0] select task;
                }
            }
            """;

        Assert.Equal(
            ["branch", "build", "card", "decision", "diff", "discovery", "epic", "evaluation", "fix", "fix", "idea", "issue", "lesson", "lesson", "merge", "rebase", "review", "task", "task", "test", "worktree"],
            Hits(source).Select(hit => hit.Noun).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void It_reports_nothing_in_a_comment()
    {
        const string source = """
            // A task, a review, and a pull request.
            /// <summary>The build and the merge.</summary>
            /* GitHub */
            static class C { }
            """;

        Assert.Empty(Hits(source));
    }

    [Fact]
    public void It_reports_nothing_in_a_name_used_but_not_declared()
    {
        const string source = """
            using System.Threading.Tasks;

            static class C
            {
                static Task M(Builder builder)
                {
                    builder.Build();
                    return Task.CompletedTask;
                }
            }
            """;

        Assert.Empty(Hits(source));
    }

    [Fact]
    public void It_reports_nothing_for_a_noun_inside_a_longer_word()
    {
        const string source = """
            sealed class Builder
            {
                void Rebuild(int decider) { }
                const string S = "Tested, untasked, rebuilding, and debranched";
            }
            """;

        Assert.Empty(Hits(source));
    }

    [Fact]
    public void It_fails_on_source_that_does_not_parse()
    {
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => Hits("static class C { void M( }"));

        Assert.StartsWith($"{File} parses with an error", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_name_is_split_at_case_changes_underscores_and_digits()
    {
        Assert.Equal(["HTTP", "Server", "build", "Test", "x"], DomainNounMatcher.Words("HTTPServer_build2Test9x"));
    }

    private static IReadOnlyList<NounHit> Hits(string source) => DomainNounMatcher.Find(File, source);
}
