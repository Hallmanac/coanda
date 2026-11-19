using Coanda.Core;
using Coanda.Core.Definitions;

namespace Coanda.Daemon.Definitions;

/// <summary>
/// The words the loopback API and <c>coa definition</c> name a definition's kind, scope, and status
/// by: <c>parcel-type</c>, <c>flow</c>, <c>agent</c>, and <c>trigger</c>; <c>project</c>,
/// <c>owner</c>, and <c>module</c>; <c>draft</c>, <c>published</c>, and <c>retired</c>. A kind or
/// scope is read whatever its case, and the name the store gives it (<c>ParcelType</c>) is read too.
/// </summary>
public static class DefinitionWords
{
    private static readonly (DefinitionKind Kind, string Word)[] Kinds =
    [
        (DefinitionKind.ParcelType, "parcel-type"),
        (DefinitionKind.Flow, "flow"),
        (DefinitionKind.Agent, "agent"),
        (DefinitionKind.Trigger, "trigger"),
    ];

    private static readonly (DefinitionScope Scope, string Word)[] Scopes =
    [
        (DefinitionScope.Project, "project"),
        (DefinitionScope.Owner, "owner"),
        (DefinitionScope.Module, "module"),
    ];

    public static string Of(DefinitionKind kind) => Kinds.Single(k => k.Kind == kind).Word;

    public static string Of(DefinitionScope scope) => Scopes.Single(s => s.Scope == scope).Word;

    public static string Of(DefinitionStatus status) => status switch
    {
        DefinitionStatus.Draft => "draft",
        DefinitionStatus.Published => "published",
        DefinitionStatus.Retired => "retired",
        _ => throw new DomainException($"'{status}' is not a definition status."),
    };

    /// <summary>The kind <paramref name="word"/> names; refused, naming every kind, when it names none.</summary>
    public static DefinitionKind KindOf(string? word) =>
        Kinds.FirstOrDefault(k => Names(k.Word, k.Kind.ToString(), word)) is { Word: not null } found
            ? found.Kind
            : throw new DomainException(
                $"'{word}' is not a kind of definition; a kind is one of {string.Join(", ", Kinds.Select(k => k.Word))}.");

    /// <summary>The scope <paramref name="word"/> names, project when it is blank; refused, naming every scope, when it names none.</summary>
    public static DefinitionScope ScopeOf(string? word) =>
        string.IsNullOrWhiteSpace(word)
            ? DefinitionScope.Project
            : Scopes.FirstOrDefault(s => Names(s.Word, s.Scope.ToString(), word)) is { Word: not null } found
                ? found.Scope
                : throw new DomainException(
                    $"'{word}' is not a definition scope; a scope is one of {string.Join(", ", Scopes.Select(s => s.Word))}.");

    private static bool Names(string word, string storedName, string? given) =>
        given is not null
        && (string.Equals(word, given.Trim(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(storedName, given.Trim(), StringComparison.OrdinalIgnoreCase));
}
