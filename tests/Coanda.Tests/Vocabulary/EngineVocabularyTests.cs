using Coanda.Tests.Sdk;

namespace Coanda.Tests.Vocabulary;

/// <summary>
/// The boundary rule's vocabulary test: Core, the daemon, the CLI, and the SDK's contract name no domain
/// noun except where an entry of <see cref="Allowed"/> allows it. Every entry names its file, the nouns it
/// allows there, and its reason. A domain-sense entry is a recorded exception with the condition that
/// ends it; an other-sense entry names the one declared name or literal it allows, which renaming would
/// change a stored shape, a module API document, hashed definition content, or a name Coanda does not own.
/// </summary>
public sealed class EngineVocabularyTests
{
    /// <summary>The folders searched, from the repository's root.</summary>
    private static readonly string[] Searched = ["src/Coanda.Core", "src/Coanda.Daemon", "src/Coanda.Cli", "src/Coanda.Sdk/Contract"];

    /// <summary>The one file in which the host composes the modules, which plan section 2.3 lets name a module and which is not engine code.</summary>
    private const string CompositionFile = "src/Coanda.Cli/ModuleComposition.cs";

    private static readonly AllowedUse[] Allowed =
    [
        AllowedUse.DomainSense(
            "src/Coanda.Daemon/SystemSteps/UnprefixedSoftwareSteps.cs",
            ["worktree", "merge", "rebase"],
            "It maps the software module's five earlier unprefixed step names (plan section 3.2 item 7, ruling 2), because a stored flow keeps its content and its hash.",
            "It ends when no stored flow run or agent in a live node home names the earlier form."),
        AllowedUse.DomainSense(
            "src/Coanda.Core/Agents/NameOnlyAnswerSchemas.cs",
            ["build", "review", "fix", "discovery"],
            "It keeps the four answer schemas agents named before an agent carried its answer schema as content (plan section 3.2 item 8, ruling 2).",
            "It ends when no stored flow run or agent in a live node home names the earlier form."),
        AllowedUse.DomainSense(
            "src/Coanda.Daemon/Store/EarlierShapes.cs",
            ["branch", "build", "test"],
            "The members of a registration stored before repos, which the earlier-shape check names so a home holding one is refused with a rebuild.",
            "It ends when the daemon no longer recognizes a registration from before repos as an earlier shape.",
            ["primaryBranch", "buildCommand", "testCommand"]),
        AllowedUse.OtherSense(
            "src/Coanda.Daemon/Store/RecordExportFile.cs",
            ["build"],
            ["Build"],
            "The version of coa that wrote an export: a member of the record export file's format, which renaming would change."),
        AllowedUse.OtherSense(
            "src/Coanda.Sdk/Contract/ApprovalRecord.cs",
            ["review"],
            ["ReviewId"],
            "The member that carries reviewId in schemas/module/v1/approval.schema.json, a module API document."),
        AllowedUse.OtherSense(
            "src/Coanda.Daemon/Executors/ClaudeCode/ShellWords.cs",
            ["test"],
            ["test"],
            "The shell builtin test, a name Coanda does not own."),
    ];

    [Fact]
    public void The_engine_names_no_domain_noun_that_no_entry_allows()
    {
        string[] unallowed = [.. EngineHits().Where(hit => !Allowed.Any(entry => entry.Allows(hit))).Select(hit => hit.ToString())];

        Assert.True(unallowed.Length == 0, $"The engine names a domain noun no entry allows:\n{string.Join("\n", unallowed)}");
    }

    [Fact]
    public void Every_entry_allows_only_what_the_search_still_finds_in_its_file()
    {
        NounHit[] hits = [.. EngineHits()];
        List<string> stale = [];
        foreach (AllowedUse entry in Allowed)
        {
            NounHit[] allowed = [.. hits.Where(entry.Allows)];
            stale.AddRange(entry.Nouns.Where(noun => !allowed.Any(hit => hit.Noun == noun)).Select(noun => $"{entry.File} allows '{noun}', which the search no longer finds there."));
            stale.AddRange((entry.Texts ?? []).Where(text => !allowed.Any(hit => hit.Text == text)).Select(text => $"{entry.File} names \"{text}\", which the search no longer finds there."));
        }

        Assert.True(stale.Count == 0, string.Join("\n", stale));
    }

    [Fact]
    public void Exactly_three_entries_allow_a_domain_noun_in_its_domains_sense_and_each_says_when_it_ends()
    {
        AllowedUse[] domain = [.. Allowed.Where(entry => entry.Ends is not null)];

        Assert.Equal(
            ["src/Coanda.Core/Agents/NameOnlyAnswerSchemas.cs", "src/Coanda.Daemon/Store/EarlierShapes.cs", "src/Coanda.Daemon/SystemSteps/UnprefixedSoftwareSteps.cs"],
            domain.Select(entry => entry.File).Order(StringComparer.Ordinal));
        Assert.All(domain, entry => Assert.StartsWith("It ends when ", entry.Ends, StringComparison.Ordinal));
    }

    [Fact]
    public void Every_other_entry_names_the_declared_name_or_literal_it_allows()
    {
        Assert.All(
            Allowed.Where(entry => entry.Ends is null),
            entry => Assert.True(entry.Texts is { Count: > 0 }, $"{entry.File} allows a noun in another sense without naming the name or literal it allows."));
    }

    [Fact]
    public void Every_entry_names_a_listed_noun_and_a_reason()
    {
        Assert.All(Allowed, entry =>
        {
            Assert.NotEmpty(entry.Nouns);
            Assert.All(entry.Nouns, noun => Assert.Contains(noun, DomainNounMatcher.NounList));
            Assert.False(string.IsNullOrWhiteSpace(entry.Reason));
        });
    }

    /// <summary>Every hit in every searched file, failing on the first file that parses with an error.</summary>
    private static IEnumerable<NounHit> EngineHits()
    {
        foreach (string file in SearchedFiles())
        {
            foreach (NounHit hit in DomainNounMatcher.Find(file, File.ReadAllText(RepositoryFiles.PathOf(file))))
            {
                yield return hit;
            }
        }
    }

    /// <summary>The searched files, each as its path from the repository's root with forward slashes.</summary>
    private static string[] SearchedFiles()
    {
        string[] files =
        [
            .. Searched.SelectMany(folder =>
            {
                string root = RepositoryFiles.PathOf(folder);
                return Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories).Where(file => !IsBuildOutput(root, file));
            })
            .Select(file => Path.GetRelativePath(RepositoryFiles.Root, file).Replace('\\', '/'))
            .Where(file => file != CompositionFile)
            .Order(StringComparer.Ordinal),
        ];

        Assert.NotEmpty(files);
        return files;
    }

    private static bool IsBuildOutput(string folder, string file)
    {
        string first = Path.GetRelativePath(folder, file).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return first is "bin" or "obj";
    }

    /// <summary>
    /// One entry of the list. <see cref="Texts"/>, when it is set, is the declared names or literal texts the
    /// entry allows its nouns in; when it is null the entry allows its nouns anywhere in its file, which only a
    /// domain-sense entry may do. <see cref="Ends"/> is set on a domain-sense entry alone.
    /// </summary>
    private sealed record AllowedUse(string File, IReadOnlyList<string> Nouns, IReadOnlyList<string>? Texts, string Reason, string? Ends)
    {
        public static AllowedUse DomainSense(string file, string[] nouns, string reason, string ends, string[]? texts = null) =>
            new(file, nouns, texts, reason, ends);

        public static AllowedUse OtherSense(string file, string[] nouns, string[] texts, string reason) =>
            new(file, nouns, texts, reason, Ends: null);

        public bool Allows(NounHit hit) =>
            hit.File == File
                && Nouns.Contains(hit.Noun, StringComparer.Ordinal)
                && (Texts is null || Texts.Contains(hit.Text, StringComparer.Ordinal));
    }
}
