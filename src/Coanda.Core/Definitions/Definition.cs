using System.Text.Json.Serialization;
using Coanda.Core.Definitions.Events;

namespace Coanda.Core.Definitions;

/// <summary>
/// A parcel type, flow, or agent held in the store. State changes only through <c>Apply</c>; the
/// rules live in <see cref="DefinitionDecider"/>. The store snapshots this class inline, so it is
/// also the document a resolution query reads.
/// </summary>
public sealed class Definition
{
    [JsonInclude]
    public Guid Id { get; private set; }
    [JsonInclude]
    public DefinitionKind Kind { get; private set; }
    [JsonInclude]
    public string Name { get; private set; } = string.Empty;
    [JsonInclude]
    public DefinitionScope Scope { get; private set; }
    /// <summary>The project the definition belongs to when its scope is project; otherwise null.</summary>
    [JsonInclude]
    public string? Project { get; private set; }
    /// <summary>
    /// Counts from 1 per kind, scope, and name, and per project in project scope. Not called
    /// <c>Version</c>: the store gives an aggregate property of that name the stream's event
    /// count, which would overwrite this.
    /// </summary>
    [JsonInclude]
    public int VersionNumber { get; private set; }
    [JsonInclude]
    public string Content { get; private set; } = string.Empty;
    [JsonInclude]
    public string ContentHash { get; private set; } = string.Empty;
    [JsonInclude]
    public DefinitionStatus Status { get; private set; }
    [JsonInclude]
    public ForkBase? ForkBase { get; private set; }
    [JsonInclude]
    public string DraftedBy { get; private set; } = string.Empty;
    [JsonInclude]
    public DateTimeOffset DraftedAt { get; private set; }
    [JsonInclude]
    public DateTimeOffset? PublishedAt { get; private set; }
    [JsonInclude]
    public DateTimeOffset? RetiredAt { get; private set; }

    public static Definition Create(DefinitionDrafted drafted)
    {
        var definition = new Definition();
        definition.Apply(drafted);
        return definition;
    }

    public void Apply(DefinitionDrafted drafted)
    {
        Id = drafted.DefinitionId;
        Kind = drafted.Kind;
        Name = drafted.Name;
        Scope = drafted.Scope;
        Project = drafted.Project;
        VersionNumber = drafted.Version;
        Content = drafted.Content;
        ContentHash = drafted.ContentHash;
        ForkBase = drafted.ForkBase;
        Status = DefinitionStatus.Draft;
        DraftedBy = drafted.DraftedBy;
        DraftedAt = drafted.DraftedAt;
    }

    public void Apply(DefinitionPublished published)
    {
        Status = DefinitionStatus.Published;
        PublishedAt = published.PublishedAt;
    }

    public void Apply(DefinitionRetired retired)
    {
        Status = DefinitionStatus.Retired;
        RetiredAt = retired.RetiredAt;
    }
}
