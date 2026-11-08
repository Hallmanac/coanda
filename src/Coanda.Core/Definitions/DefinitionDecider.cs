using System.Collections.Frozen;
using Coanda.Core.Agents;
using Coanda.Core.Definitions.Events;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Core.Types;

namespace Coanda.Core.Definitions;

/// <summary>
/// The rules of a definition's life, as pure decisions: the event that records an intent, or a
/// <see cref="DomainException"/> quoting the rule. The one road into module scope is
/// <see cref="Import"/>; a draft can never name it.
/// </summary>
public static class DefinitionDecider
{
    /// <summary>
    /// Drafts the next version of a definition. <paramref name="latestVersion"/> is the highest
    /// version already recorded for the same kind, scope, and name (and project, in project
    /// scope), or 0 when there is none. <paramref name="knownGates"/> is every gate name the
    /// daemon runs, which a parcel type's gate bindings are validated against; the shipped gate set's when null.
    /// <paramref name="knownSystemSteps"/> is every system step name the daemon registered, which a flow's
    /// system steps are validated against; none when null. <paramref name="stepArguments"/> is what each
    /// system step's argument schema says of a step's arguments; no step's arguments are checked against a
    /// schema when null. The decider holds no catalog of any of them.
    /// <paramref name="moduleVersions"/> is every module-scope definition of <paramref name="kind"/>,
    /// which a fork base must name one of by name, module, module version, and content hash, and whose parcel type, when the fork
    /// base names one, is the only source of a field owner the draft's parcel type may name; none when null.
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
        IReadOnlySet<string>? knownSystemSteps = null,
        IReadOnlyCollection<Definition>? moduleVersions = null,
        StepArgumentCheck? stepArguments = null)
    {
        if (scope == DefinitionScope.Module)
        {
            throw new DomainException(
                "A definition cannot be drafted into module scope; module-scope definitions enter the store only when a module's definitions are imported.");
        }

        Definition? forkedFrom = ForkedFrom(kind, name, forkBase, moduleVersions ?? []);
        return Build(
            kind, name, scope, project, latestVersion, content, contentHash, forkBase,
            type => RequireOwnersFromForkBase(type, forkBase, forkedFrom),
            knownGates ?? GateSet.Shipped.Names, knownSystemSteps ?? FrozenSet<string>.Empty, stepArguments, draftedBy, now, null, null);
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
        if (definition.Scope == DefinitionScope.Module)
        {
            throw new DomainException(
                $"A module-scope definition cannot be retired; '{definition.Name}' version {definition.VersionNumber} came with module '{definition.Module}' version {definition.ModuleVersion}.");
        }

        if (definition.Status != DefinitionStatus.Published)
        {
            throw new DomainException(
                $"Only a published definition can be retired; '{definition.Name}' version {definition.VersionNumber} is {Describe(definition.Status)}.");
        }

        return new DefinitionRetired(definition.Id, retiredBy, now);
    }

    /// <summary>
    /// Imports one definition the module <paramref name="module"/> brings, at its version
    /// <paramref name="moduleVersion"/>: the drafted and published events together, in module scope,
    /// carrying the module's name and version. The only road into that scope. <paramref name="held"/>
    /// is every module-scope definition of the same kind and name the store holds, of any module: the
    /// import is refused when another module's is among them, is null when any of the module's own
    /// versions already has the content hash, as seeding did, and is otherwise the next version after
    /// the highest of them. Each
    /// definition passes the validation a draft of no project does: a parcel type is validated against
    /// <paramref name="knownGates"/>, the gates the daemon runs (the shipped gate set's when null), and
    /// may name only its own module as a field's owner; a flow is validated against
    /// <paramref name="knownSystemSteps"/>, the system step names a flow of no project may name (none when
    /// null), and its system steps' arguments against <paramref name="stepArguments"/> (unchecked when null).
    /// </summary>
    public static (DefinitionDrafted Drafted, DefinitionPublished Published)? Import(
        ModuleDefinition definition,
        string module,
        string moduleVersion,
        IReadOnlyCollection<Definition> held,
        string importedBy,
        DateTimeOffset now,
        IReadOnlySet<string>? knownGates = null,
        IReadOnlySet<string>? knownSystemSteps = null,
        StepArgumentCheck? stepArguments = null)
    {
        ModuleNames.Require(module);
        if (string.IsNullOrWhiteSpace(moduleVersion))
        {
            throw new DomainException($"Module '{module}' has no version; a module's definitions are imported at the version they came with.");
        }

        Definition[] versions =
        [
            .. held.Where(d => d.Scope == DefinitionScope.Module && d.Kind == definition.Kind && DefinitionResolution.SameName(d.Name, definition.Name)),
        ];
        if (versions.FirstOrDefault(d => !string.Equals(d.Module, module, StringComparison.Ordinal)) is { } other)
        {
            throw new DomainException(
                $"Module '{module}' brings the {Describe(definition.Kind)} '{definition.Name}', which module '{other.Module}' already brought; a definition's name is held by one module in module scope.");
        }

        if (versions.Any(d => string.Equals(d.ContentHash, definition.ContentHash, StringComparison.Ordinal)))
        {
            return null;
        }

        DefinitionDrafted drafted = Build(
            definition.Kind, definition.Name, DefinitionScope.Module, null, versions.Select(d => d.VersionNumber).DefaultIfEmpty(0).Max(),
            definition.Content, definition.ContentHash, null, type => RequireOwnModuleOnly(type, module),
            knownGates ?? GateSet.Shipped.Names, knownSystemSteps ?? FrozenSet<string>.Empty, stepArguments, importedBy, now, module, moduleVersion.Trim());
        return (drafted, new DefinitionPublished(drafted.DefinitionId, importedBy, now));
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
        Action<ParcelType> requireOwners,
        IReadOnlySet<string> knownGates,
        IReadOnlySet<string> knownSystemSteps,
        StepArgumentCheck? stepArguments,
        string draftedBy,
        DateTimeOffset now,
        string? module,
        string? moduleVersion)
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

        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(contentHash))
        {
            throw new DomainException($"A definition needs content and a content hash; '{trimmedName}' lacks one.");
        }

        switch (kind)
        {
            case DefinitionKind.ParcelType:
                requireOwners(RequireParcelType(trimmedName, content, contentHash, knownGates));
                break;
            case DefinitionKind.Flow:
                RequireFlow(trimmedName, content, contentHash, knownSystemSteps, stepArguments);
                break;
            case DefinitionKind.Agent:
                RequireAgent(trimmedName, content, contentHash);
                break;
        }

        return new DefinitionDrafted(
            Ids.New(), kind, trimmedName, scope, trimmedProject, latestVersion + 1, content, contentHash, forkBase, draftedBy, now,
            module, moduleVersion);
    }

    /// <summary>
    /// The module-scope definition <paramref name="forkBase"/> names among <paramref name="moduleVersions"/>,
    /// or null when there is no fork base. A fork base names its base by name, module, module version,
    /// and content hash, and is refused when it lacks any of the four, or when no module-scope definition
    /// of the draft's kind matches all four. On the drafting node the module
    /// version is by construction the one that node holds the content under, so all four are matched
    /// here, although resolution elsewhere matches only the name, module, and content hash.
    /// </summary>
    private static Definition? ForkedFrom(DefinitionKind kind, string name, ForkBase? forkBase, IReadOnlyCollection<Definition> moduleVersions)
    {
        if (forkBase is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(forkBase.Name)
            || string.IsNullOrWhiteSpace(forkBase.Module)
            || string.IsNullOrWhiteSpace(forkBase.ModuleVersion)
            || string.IsNullOrWhiteSpace(forkBase.ContentHash))
        {
            throw new DomainException(
                $"A fork base names the module-scope definition a customization came from by its name, module, module version, and content hash; '{name.Trim()}' has an invalid one.");
        }

        string moduleVersion = forkBase.ModuleVersion;
        return moduleVersions.FirstOrDefault(d => d.Kind == kind
                && forkBase.IsBaseOf(d)
                && string.Equals(d.ModuleVersion, moduleVersion, StringComparison.Ordinal))
            ?? throw new DomainException(
                $"The fork base {forkBase.Describe()} of '{name.Trim()}' names no module-scope {Describe(kind)}; a fork base names a module-scope definition of the draft's own kind, by name, module, module version, and content hash.");
    }

    /// <summary>
    /// Refuses a field owner a project-scope or owner-scope type names unless its fork base declares a
    /// field of the same name owned by the same module, so a fork of a module's type keeps that field
    /// owned by that module and a user's type grants no module a field otherwise; and refuses a field
    /// that keeps its base's owner but declares a different kind, since the owning module reads and
    /// writes the field as the kind it declared.
    /// </summary>
    private static void RequireOwnersFromForkBase(ParcelType type, ForkBase? forkBase, Definition? forkedFrom)
    {
        ParcelType? baseType = forkedFrom is null ? null : ParcelType.FromJson(forkedFrom.Content);
        foreach (FieldDeclaration field in type.Fields.Where(f => f.Owner is not null))
        {
            FieldDeclaration? baseField = baseType?.Fields.FirstOrDefault(f => string.Equals(f.Name, field.Name, StringComparison.Ordinal));
            if (baseField is not null && string.Equals(baseField.Owner, field.Owner, StringComparison.Ordinal))
            {
                if (string.Equals(baseField.Kind, field.Kind, StringComparison.Ordinal))
                {
                    continue;
                }

                throw new DomainException(
                    $"Field '{field.Name}' of parcel type '{type.Name}' keeps the module '{field.Owner}' as its owner but declares the kind '{field.Kind}', and its fork base {forkBase?.Describe()} declares that field of the kind '{baseField.Kind}'; a type that keeps a field's owner from its fork base keeps the field's kind too.");
            }

            string reason = forkBase is null
                ? $"'{type.Name}' has no fork base"
                : $"its fork base {forkBase.Describe()} declares no field '{field.Name}' owned by '{field.Owner}'";
            throw new DomainException(
                $"Field '{field.Name}' of parcel type '{type.Name}' names the module '{field.Owner}' as its owner; a type of project or owner scope names a field's owner only for a field its fork base declares owned by that same module, and {reason}.");
        }
    }

    /// <summary>Refuses a module's parcel type that names another module as a field's owner.</summary>
    private static void RequireOwnModuleOnly(ParcelType type, string module)
    {
        if (type.Fields.FirstOrDefault(f => f.Owner is not null && !string.Equals(f.Owner, module, StringComparison.Ordinal)) is { } field)
        {
            throw new DomainException(
                $"Field '{field.Name}' of parcel type '{type.Name}' of module '{module}' names the module '{field.Owner}' as its owner; a module's type names only its own module as a field's owner.");
        }
    }

    private static ParcelType RequireParcelType(string name, string content, string contentHash, IReadOnlySet<string> knownGates)
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

        return type;
    }

    private static void RequireFlow(string name, string content, string contentHash, IReadOnlySet<string> knownSystemSteps, StepArgumentCheck? stepArguments)
    {
        Flow flow = Flow.FromJson(content);
        flow.Validate(knownSystemSteps, stepArguments);

        if (!string.Equals(flow.Name, name, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException($"A flow definition is named for its flow; '{name}' holds the flow '{flow.Name}'.");
        }

        if (!string.Equals(flow.Hash, contentHash, StringComparison.Ordinal))
        {
            throw new DomainException(
                $"The content hash '{contentHash}' differs from the flow's hash '{flow.Hash}'.");
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

    private static string Describe(DefinitionKind kind) => kind switch
    {
        DefinitionKind.ParcelType => "parcel type",
        DefinitionKind.Flow => "flow",
        _ => "agent",
    };

    private static string Describe(DefinitionStatus status) => status switch
    {
        DefinitionStatus.Draft => "a draft",
        DefinitionStatus.Published => "published",
        _ => "retired",
    };
}
