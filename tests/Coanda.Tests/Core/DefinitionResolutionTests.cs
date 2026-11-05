using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Definitions.Events;
using Coanda.Core.Types;

namespace Coanda.Tests.Core;

public sealed class DefinitionResolutionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private static Definition Make(
        DefinitionScope scope,
        int version,
        string name = "task",
        string? project = null,
        bool publish = true,
        bool retire = false)
    {
        ParcelType type = SoftwareTypes.Task with { Name = name };
        Definition definition = scope == DefinitionScope.Module
            ? Definition.Create(new DefinitionDrafted(
                Ids.New(), DefinitionKind.ParcelType, name, scope, null, version, ParcelType.CanonicalJson(type), type.Hash, null, "import", Now, "software", "0.1.0"))
            : Definition.Create(DefinitionDecider.Draft(
                DefinitionKind.ParcelType, name, scope, project, version - 1, ParcelType.CanonicalJson(type), type.Hash, null, "brian", Now));
        if (publish)
        {
            definition.Apply(new DefinitionPublished(definition.Id, "owner", Now));
        }

        if (retire)
        {
            definition.Apply(new DefinitionRetired(definition.Id, "owner", Now));
        }

        return definition;
    }

    private static Definition? Resolve(IEnumerable<Definition> all, string project = "alpha", string name = "task") =>
        DefinitionResolution.Resolve(all, project, DefinitionKind.ParcelType, name);

    [Fact]
    public void The_nearest_scope_with_a_published_version_wins_in_the_order_project_owner_module()
    {
        Definition shipped = Make(DefinitionScope.Module, 1);
        Definition owner = Make(DefinitionScope.Owner, 1);
        Definition project = Make(DefinitionScope.Project, 1, project: "alpha");

        Assert.Same(project, Resolve([shipped, owner, project]));
        Assert.Same(owner, Resolve([shipped, owner]));
        Assert.Same(shipped, Resolve([shipped]));
    }

    [Fact]
    public void A_nearer_scope_wins_even_over_a_higher_version_in_a_farther_one()
    {
        Definition shipped = Make(DefinitionScope.Module, 5);
        Definition project = Make(DefinitionScope.Project, 1, project: "alpha");

        Assert.Same(project, Resolve([shipped, project]));
    }

    [Fact]
    public void The_highest_published_version_within_the_scope_wins()
    {
        Definition v1 = Make(DefinitionScope.Project, 1, project: "alpha");
        Definition v2 = Make(DefinitionScope.Project, 2, project: "alpha");
        Definition v3Draft = Make(DefinitionScope.Project, 3, project: "alpha", publish: false);

        Assert.Same(v2, Resolve([v1, v3Draft, v2]));
    }

    [Fact]
    public void A_draft_or_a_retired_version_is_never_returned_and_resolution_falls_through()
    {
        Definition shipped = Make(DefinitionScope.Module, 1);
        Definition draft = Make(DefinitionScope.Project, 1, project: "alpha", publish: false);
        Definition retired = Make(DefinitionScope.Owner, 1, retire: true);

        Assert.Same(shipped, Resolve([shipped, draft, retired]));
        Assert.Null(Resolve([draft, retired]));
    }

    [Fact]
    public void Retiring_the_highest_version_returns_the_next_published_one()
    {
        Definition v1 = Make(DefinitionScope.Project, 1, project: "alpha");
        Definition v2 = Make(DefinitionScope.Project, 2, project: "alpha", retire: true);

        Assert.Same(v1, Resolve([v1, v2]));
    }

    [Fact]
    public void A_project_definition_is_never_returned_for_another_project()
    {
        Definition shipped = Make(DefinitionScope.Module, 1);
        Definition alphas = Make(DefinitionScope.Project, 1, project: "alpha");

        Assert.Same(shipped, Resolve([shipped, alphas], project: "beta"));
        Assert.Null(Resolve([alphas], project: "beta"));
    }

    [Fact]
    public void The_name_matches_case_insensitively()
    {
        Definition shipped = Make(DefinitionScope.Module, 1);

        Assert.Same(shipped, Resolve([shipped], name: "TASK"));
        Assert.Same(shipped, Resolve([shipped], name: "Task"));
        Assert.Null(Resolve([shipped], name: "idea"));
    }

    [Fact]
    public void Resolution_is_per_kind()
    {
        Definition shipped = Make(DefinitionScope.Module, 1);

        Assert.Null(DefinitionResolution.Resolve([shipped], "alpha", DefinitionKind.Flow, "task"));
    }

    [Fact]
    public void Resolving_all_returns_one_definition_per_name_ordered_by_name()
    {
        Definition task = Make(DefinitionScope.Module, 1);
        Definition idea = Make(DefinitionScope.Module, 1, name: "idea");
        Definition alphaTask = Make(DefinitionScope.Project, 1, project: "alpha");

        IReadOnlyList<Definition> forAlpha = DefinitionResolution.ResolveAll([task, idea, alphaTask], "alpha", DefinitionKind.ParcelType);
        Assert.Equal([idea, alphaTask], forAlpha);

        IReadOnlyList<Definition> forBeta = DefinitionResolution.ResolveAll([task, idea, alphaTask], "beta", DefinitionKind.ParcelType);
        Assert.Equal([idea, task], forBeta);
    }
}
