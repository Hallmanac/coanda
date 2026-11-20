using System.Text.Json;
using Coanda.Daemon.Modules;
using Coanda.Sdk.Contract;
using Coanda.Tests.Vocabulary;

namespace Coanda.Tests.Harbor;

/// <summary>
/// The harbor module passes the boundary rule's noun test (plan section 2.2): its <c>module.json</c>, every file
/// under its definitions folder, and its describe answer as the module API's JSON name no domain noun of the
/// vocabulary test's list, matched as that test matches a noun in a string literal.
/// </summary>
public sealed class HarborVocabularyTests
{
    [Fact]
    public async Task The_harbor_module_names_no_domain_noun_in_its_manifest_its_definitions_or_its_describe_answer()
    {
        string definitions = Path.Combine(HarborModule.Path, ModuleArtifact.DefinitionsFolder);
        string[] files =
        [
            Path.Combine(HarborModule.Path, ModuleArtifact.ManifestFile),
            .. Directory.EnumerateFiles(definitions, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal),
        ];
        Assert.Contains(files, file => file.EndsWith(Path.Combine("triggers", "docked.json"), StringComparison.Ordinal));
        using var harbor = new HarborModule();
        DescribeAnswer described = await harbor.DescribeAsync(CancellationToken.None);

        List<(string Source, string Text)> read =
        [
            .. files.Select(file => (Path.GetRelativePath(HarborModule.Path, file), File.ReadAllText(file))),
            ("the describe answer", JsonSerializer.Serialize(described, ModuleJson.Options)),
        ];
        string[] found =
        [
            .. read.SelectMany(source => DomainNounMatcher.NounsIn(source.Text)
                .Select(hit => $"{source.Source}: '{hit.Noun}' in the word{(hit.Words.Contains(' ', StringComparison.Ordinal) ? "s" : string.Empty)} \"{hit.Words}\"")),
        ];

        Assert.True(found.Length == 0, $"The harbor module names a domain noun:\n{string.Join("\n", found)}");
    }
}
