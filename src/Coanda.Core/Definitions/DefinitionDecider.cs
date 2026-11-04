using System.Collections.Frozen;
using Coanda.Core.Agents;
using Coanda.Core.Definitions.Events;
using Coanda.Core.Contours;
using Coanda.Core.Parcels;
using Coanda.Core.Types;

namespace Coanda.Core.Definitions;

/// <summary>
/// The rules of a definition's life, as pure decisions: the event that records an intent, or a
/// <see cref="DomainException"/> quoting the rule. The one road into shipped scope is
/// <see cref="Seed"/>; a draft can never name it.
/// </summary>
public static class DefinitionDecider
{
    /// <summary>
    /// Drafts the next version of a definition. <paramref name="latestVersion"/> is the highest
    /// version already recorded for the same kind, scope, and name (and project, in project
    /// scope), or 0 when there is none. <paramref name="knownGates"/> is every gate name the
    /// daemon runs, which a parcel type's gate bindings are validated against; none when null.
    /// <paramref name="knownActions"/> is every action name the daemon registered, which a contour's
    /// action steps are validated against; none when null. The decider holds no catalog of either.
    /// </summary>
    public static DefinitionDrafted Draft(
        DefinitionKind kind,
        string name,
        DefinitionScope scope,
        string? project,
        int latestVersion,
        string content,
        string contentHash,
        ForkBase? forkBase,
        string draftedBy,
        DateTimeOffset now,
        IReadOnlySet<string>? knownGates = null,
        IReadOnlySet<string>? knownActions = null)
    {
        if (scope == DefinitionScope.Shipped)
        {
            throw new DomainException(
                "A definition cannot be drafted into shipped scope; shipped definitions enter the store only by seeding.");
        }

        return Build(kind, name, scope, project, latestVersion, content, contentHash, forkBase, knownGates ?? GateSet.None.Names, knownActions ?? FrozenSet<string>.Empty, draftedBy, now);
    }

    public static DefinitionPublished Publish(Definition definition, string publishedBy, DateTimeOffset now)
    {
        if (definition.Status != DefinitionStatus.Draft)
        {
            throw new DomainException(
                $"Only a draft can be published; '{definition.Name}' version {definition.VersionNumber} is {Describe(definition.Status)}.");
        }

        return new DefinitionPublished(definition.Id, publishedBy, now);
    }

    public static DefinitionRetired Retire(Definition definition, string retiredBy, DateTimeOffset now)
    {
        if (definition.Scope == DefinitionScope.Shipped)
        {
            throw new DomainException(
                $"A shipped definition cannot be retired; '{definition.Name}' version {definition.VersionNumber} is carried by the binary.");
        }

        if (definition.Status != DefinitionStatus.Published)
        {
            throw new DomainException(
                $"Only a published definition can be retired; '{definition.Name}' version {definition.VersionNumber} is {Describe(definition.Status)}.");
        }

        return new DefinitionRetired(definition.Id, retiredBy, now);
    }

    /// <summary>
    /// Seeds a shipped definition: the drafted and published events together, in shipped scope.
    /// The only road into that scope. The shipped gate set is empty, so a shipped parcel type is
    /// validated against no gate names and binds none. A shipped contour is validated against
    /// <paramref name="knownActions"/>, the action names the daemon registered; none when null.
    /// </summary>
    public static (DefinitionDrafted Drafted, DefinitionPublished Published) Seed(
        ShippedDefinition shipped, int latestVersion, string seededBy, DateTimeOffset now, IReadOnlySet<string>? knownActions = null)
    {
        DefinitionDrafted drafted = Build(
            shipped.Kind, shipped.Name, DefinitionScope.Shipped, null, latestVersion,
            shipped.Content, shipped.ContentHash, null, GateSet.None.Names, knownActions ?? FrozenSet<string>.Empty, seededBy, now);
        return (drafted, new DefinitionPublished(drafted.DefinitionId, seededBy, now));
    }

    private static DefinitionDrafted Build(
        DefinitionKind kind,
        string name,
        DefinitionScope scope,
        string? project,
        int latestVersion,
        string content,
        string contentHash,
        ForkBase? forkBase,
        IReadOnlySet<string> knownGates,
        IReadOnlySet<string> knownActions,
        string draftedBy,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new DomainException("A definition needs a name.");
        }

        string trimmedName = name.Trim();
        string? trimmedProject = project?.Trim();
        switch (scope)
        {
            case DefinitionScope.Project when string.IsNullOrEmpty(trimmedProject):
                throw new DomainException($"A project-scope definition belongs to a project; none was given for '{trimmedName}'.");
            case not DefinitionScope.Project when !string.IsNullOrEmpty(trimmedProject):
                throw new DomainException($"Only a project-scope definition belongs to a project; '{trimmedName}' is {scope} scope.");
        }

        if (forkBase is not null && (scope == DefinitionScope.Shipped || string.IsNullOrWhiteSpace(forkBase.Name) || forkBase.Version < 1))
        {
            throw new DomainException(
                $"A fork base names the shipped definition and version a customization came from; '{trimmedName}' has an invalid one.");
        }

        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(contentHash))
        {
            throw new DomainException($"A definition needs content and a content hash; '{trimmedName}' lacks one.");
        }

        switch (kind)
        {
            case DefinitionKind.ParcelType:
                RequireParcelType(trimmedName, content, contentHash, knownGates);
                break;
            case DefinitionKind.Contour:
                RequireContour(trimmedName, content, contentHash, knownActions);
                break;
            case DefinitionKind.Agent:
                RequireAgent(trimmedName, content, contentHash);
                break;
        }

        return new DefinitionDrafted(
            Ids.New(), kind, trimmedName, scope, trimmedProject, latestVersion + 1, content, contentHash, forkBase, draftedBy, now);
    }

    private static void RequireParcelType(string name, string content, string contentHash, IReadOnlySet<string> knownGates)
    {
        ParcelType type = ParcelType.FromJson(content);
        type.Validate(knownGates);

        if (!string.Equals(type.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException($"A parcel-type definition is named for its type; '{name}' holds the type '{type.Name}'.");
        }

        if (!string.Equals(type.Hash, contentHash, StringComparison.Ordinal))
        {
            throw new DomainException(
                $"The content hash '{contentHash}' differs from the parcel type's hash '{type.Hash}'.");
        }
    }

    private static void RequireContour(string name, string content, string contentHash, IReadOnlySet<string> knownActions)
    {
        Contour contour = Contour.FromJson(content);
        contour.Validate(knownActions);

        if (!string.Equals(contour.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException($"A contour definition is named for its contour; '{name}' holds the contour '{contour.Name}'.");
        }

        if (!string.Equals(contour.Hash, contentHash, StringComparison.Ordinal))
        {
            throw new DomainException(
                $"The content hash '{contentHash}' differs from the contour's hash '{contour.Hash}'.");
        }
    }

    private static void RequireAgent(string name, string content, string contentHash)
    {
        AgentDefinition agent = AgentDefinition.FromJson(content);
        agent.Validate();

        if (!string.Equals(agent.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException($"An agent definition is named for its agent; '{name}' holds the agent '{agent.Name}'.");
        }

        if (!string.Equals(agent.Hash, contentHash, StringComparison.Ordinal))
        {
            throw new DomainException(
                $"The content hash '{contentHash}' differs from the agent's hash '{agent.Hash}'.");
        }
    }

    private static string Describe(DefinitionStatus status) => status switch
    {
        DefinitionStatus.Draft => "a draft",
        DefinitionStatus.Published => "published",
        _ => "retired",
    };
}
