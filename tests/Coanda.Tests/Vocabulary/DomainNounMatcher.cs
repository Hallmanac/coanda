using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Coanda.Tests.Vocabulary;

/// <summary>Where a domain noun was found: the file, the line, the noun as the list names it, and the declared name or literal text it was found in.</summary>
public sealed record NounHit(string File, int Line, string Noun, NounSite Site, string Text)
{
    public override string ToString() =>
        $"{File}:{Line}: '{Noun}' in the {(Site == NounSite.DeclaredName ? "declared name" : "literal")} {Quoted(Text)}";

    private static string Quoted(string text)
    {
        string flat = text.ReplaceLineEndings(" ");
        return flat.Length <= 120
            ? $"\"{flat}\""
            : $"\"{flat[..117]}...\"";
    }
}

/// <summary>Whether a hit was in a name the file declares or in the text of a string literal.</summary>
public enum NounSite
{
    DeclaredName,
    Literal,
}

/// <summary>
/// Finds the domain nouns of the boundary rule in C# source. It reads syntax alone: every name the file
/// declares, and the text of every string literal, including the literal parts of interpolated and raw
/// strings. It does not read comments, or a name the file only uses.
/// </summary>
public static partial class DomainNounMatcher
{
    /// <summary>Each noun as the list names it, with the word sequences that match it, singular and plural.</summary>
    private static readonly (string Noun, string[][] Words)[] Nouns =
    [
        Single("task", "tasks"),
        Single("idea", "ideas"),
        Single("discovery", "discoveries"),
        Single("review", "reviews"),
        Single("build", "builds"),
        Single("fix", "fixes"),
        Single("test", "tests"),
        Single("branch", "branches"),
        Single("worktree", "worktrees"),
        ("pull request", [["pull", "request"], ["pull", "requests"]]),
        Single("merge", "merges"),
        Single("rebase", "rebases"),
        Single("diff", "diffs"),
        ("GitHub", [["github"], ["githubs"], ["git", "hub"], ["git", "hubs"]]),
        Single("issue", "issues"),
        Single("card", "cards"),
        Single("decision", "decisions"),
        Single("lesson", "lessons"),
        Single("evaluation", "evaluations"),
        Single("epic", "epics"),
    ];

    /// <summary>The nouns as the list names them.</summary>
    public static IReadOnlyList<string> NounList { get; } = [.. Nouns.Select(noun => noun.Noun)];

    /// <summary>
    /// The domain nouns in <paramref name="source"/>, read as the file at <paramref name="file"/>. Throws,
    /// naming the file, when the source parses with an error, because a parse that recovers may drop
    /// declarations and so hide a noun.
    /// </summary>
    public static IReadOnlyList<NounHit> Find(string file, string source)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview), file);
        Diagnostic[] errors = [.. tree.GetDiagnostics().Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)];
        if (errors.Length > 0)
        {
            throw new InvalidOperationException(
                $"{file} parses with an error, so its declarations cannot be trusted: {string.Join("; ", errors.Select(error => error.ToString()))}");
        }

        List<NounHit> hits = [];
        SyntaxNode root = tree.GetRoot();
        foreach (SyntaxToken name in DeclaredNames(root))
        {
            AddHits(hits, tree, file, name, name.ValueText, NounSite.DeclaredName);
        }

        foreach (SyntaxToken literal in root.DescendantTokens().Where(IsLiteral))
        {
            AddHits(hits, tree, file, literal, literal.ValueText, NounSite.Literal);
        }

        return [.. hits.OrderBy(hit => hit.Line).ThenBy(hit => hit.Noun, StringComparer.Ordinal)];
    }

    /// <summary>The words of <paramref name="text"/>: split at every character that is not a letter, then each run of letters at case changes.</summary>
    public static IReadOnlyList<string> Words(string text) =>
        [.. LetterRuns().Matches(text).SelectMany(run => CaseWords().Matches(run.Value).Select(word => word.Value))];

    private static void AddHits(List<NounHit> hits, SyntaxTree tree, string file, SyntaxToken token, string text, NounSite site)
    {
        IReadOnlyList<string> words = Words(text);
        for (int at = 0; at < words.Count; at++)
        {
            foreach ((string noun, string[][] forms) in Nouns)
            {
                if (forms.Any(form => StartsAt(words, at, form)))
                {
                    hits.Add(new NounHit(file, LineOf(tree, token, words[at], at, words), noun, site, text));
                }
            }
        }
    }

    private static bool StartsAt(IReadOnlyList<string> words, int at, string[] form) =>
        at + form.Length <= words.Count
            && form.Select((word, offset) => string.Equals(words[at + offset], word, StringComparison.OrdinalIgnoreCase)).All(same => same);

    /// <summary>
    /// The line of the word in the token's source text, so a hit in a multi-line literal names the line the word is on.
    /// The source text is split into words the same way, with each escape sequence's letter masked so it does not join
    /// the word after it, and the word is found as a whole word, never inside a longer one. When the source text holds
    /// fewer such words than the value does, the hit names the token's first line.
    /// </summary>
    private static int LineOf(SyntaxTree tree, SyntaxToken token, string word, int index, IReadOnlyList<string> words)
    {
        int occurrence = words.Take(index).Count(earlier => string.Equals(earlier, word, StringComparison.OrdinalIgnoreCase));
        string masked = HasEscapes(token)
            ? Escapes().Replace(token.Text, escape => new string(' ', escape.Length))
            : token.Text;
        int[] positions =
        [
            .. LetterRuns().Matches(masked)
                .SelectMany(run => CaseWords().Matches(run.Value).Select(found => (Word: found.Value, Position: run.Index + found.Index)))
                .Where(found => string.Equals(found.Word, word, StringComparison.OrdinalIgnoreCase))
                .Select(found => found.Position),
        ];

        int start = occurrence < positions.Length
            ? token.SpanStart + positions[occurrence]
            : token.SpanStart;
        return tree.GetLineSpan(new TextSpan(start, 0)).StartLinePosition.Line + 1;
    }

    /// <summary>Whether a backslash in the token's source text starts an escape sequence: in a regular string, but not a verbatim or raw one.</summary>
    private static bool HasEscapes(SyntaxToken token) =>
        token.Kind() switch
        {
            SyntaxKind.StringLiteralToken or SyntaxKind.Utf8StringLiteralToken => !token.Text.StartsWith('@'),
            SyntaxKind.InterpolatedStringTextToken => token.Parent?.Parent is InterpolatedStringExpressionSyntax { StringStartToken.RawKind: (int)SyntaxKind.InterpolatedStringStartToken },
            _ => false,
        };

    private static bool IsLiteral(SyntaxToken token) =>
        token.Kind() is SyntaxKind.StringLiteralToken
            or SyntaxKind.Utf8StringLiteralToken
            or SyntaxKind.SingleLineRawStringLiteralToken
            or SyntaxKind.MultiLineRawStringLiteralToken
            or SyntaxKind.Utf8SingleLineRawStringLiteralToken
            or SyntaxKind.Utf8MultiLineRawStringLiteralToken
            or SyntaxKind.InterpolatedStringTextToken;

    /// <summary>Every token at which the file introduces a name.</summary>
    private static IEnumerable<SyntaxToken> DeclaredNames(SyntaxNode root)
    {
        foreach (SyntaxNode node in root.DescendantNodes())
        {
            IEnumerable<SyntaxToken> declared = node switch
            {
                BaseNamespaceDeclarationSyntax space => space.Name.DescendantTokens().Where(token => token.IsKind(SyntaxKind.IdentifierToken)),
                UsingDirectiveSyntax { Alias: { } alias } => [alias.Name.Identifier],
                ExternAliasDirectiveSyntax alias => [alias.Identifier],
                BaseTypeDeclarationSyntax type => [type.Identifier],
                DelegateDeclarationSyntax type => [type.Identifier],
                TypeParameterSyntax parameter => [parameter.Identifier],
                MethodDeclarationSyntax method => [method.Identifier],
                PropertyDeclarationSyntax property => [property.Identifier],
                EventDeclarationSyntax @event => [@event.Identifier],
                EnumMemberDeclarationSyntax member => [member.Identifier],
                VariableDeclaratorSyntax variable => [variable.Identifier],
                LocalFunctionStatementSyntax function => [function.Identifier],
                ParameterSyntax parameter => [parameter.Identifier],
                SingleVariableDesignationSyntax designation => [designation.Identifier],
                ForEachStatementSyntax loop => [loop.Identifier],
                CatchDeclarationSyntax declaration => [declaration.Identifier],
                TupleElementSyntax element => [element.Identifier],
                ArgumentSyntax { NameColon: { } name, Parent: TupleExpressionSyntax } => [name.Name.Identifier],
                AnonymousObjectMemberDeclaratorSyntax { NameEquals: { } name } => [name.Name.Identifier],
                LabeledStatementSyntax label => [label.Identifier],
                FromClauseSyntax clause => [clause.Identifier],
                LetClauseSyntax clause => [clause.Identifier],
                JoinClauseSyntax clause => [clause.Identifier],
                JoinIntoClauseSyntax clause => [clause.Identifier],
                QueryContinuationSyntax clause => [clause.Identifier],
                _ => [],
            };

            foreach (SyntaxToken token in declared.Where(token => !token.IsMissing && token.ValueText.Length > 0))
            {
                yield return token;
            }
        }
    }

    private static (string, string[][]) Single(string singular, string plural) =>
        (singular, [[singular], [plural]]);

    [GeneratedRegex(@"\\.")]
    private static partial Regex Escapes();

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex LetterRuns();

    [GeneratedRegex(@"\p{Lu}+(?!\p{Ll})|\p{Lu}?\p{Ll}+|\p{L}+")]
    private static partial Regex CaseWords();
}
