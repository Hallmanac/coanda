using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Definitions.Events;
using Coanda.Core.Flows;
using Coanda.Core.Registry;
using Coanda.Core.Types;
using Coanda.Modules.Software;

namespace Coanda.Tests.Core;

public sealed class DefinitionDeciderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    private static DefinitionDrafted DraftTask(ParcelType? type = null, DefinitionScope scope = DefinitionScope.Project, string? project = "alpha", int latest = 0)
    {
        ParcelType task = type ?? SoftwareTypes.UnownedTask;
        return DefinitionDecider.Draft(
            DefinitionKind.ParcelType, task.Name, scope, scope == DefinitionScope.Project ? project : null, latest,
            ParcelType.CanonicalJson(task), task.Hash, null, "brian", Now);
    }

    private static Definition Draft(int latest = 0, DefinitionScope scope = DefinitionScope.Project) =>
        Definition.Create(DraftTask(scope: scope, latest: latest));

    private static readonly ParcelType ForkedTask = SoftwareTypes.UnownedTask with { Stages = [.. SoftwareTypes.UnownedTask.Stages, "qa"] };

    private static DefinitionDrafted DraftFork(ForkBase? forkBase, IReadOnlyCollection<Definition>? moduleVersions, ParcelType? fork = null, DefinitionScope scope = DefinitionScope.Project)
    {
        ParcelType type = fork ?? ForkedTask;
        return DefinitionDecider.Draft(
            DefinitionKind.ParcelType, type.Name, scope, scope == DefinitionScope.Project ? "alpha" : null, 0,
            ParcelType.CanonicalJson(type), type.Hash, forkBase, "brian", Now, moduleVersions: moduleVersions);
    }

    [Fact]
    public void Draft_records_every_field_and_the_next_version()
    {
        Definition taskBase = ModuleVersions.Of(ModuleDefinition.FromParcelType(SoftwareTypes.UnownedTask));
        DefinitionDrafted drafted = DefinitionDecider.Draft(
            DefinitionKind.ParcelType, "task", DefinitionScope.Project, " alpha ", 2,
            ParcelType.CanonicalJson(ForkedTask), ForkedTask.Hash, ForkBase.Of(taskBase), "brian", Now, moduleVersions: [taskBase]);

        Definition definition = Definition.Create(drafted);
        Assert.Equal(DefinitionKind.ParcelType, definition.Kind);
        Assert.Equal("task", definition.Name);
        Assert.Equal(DefinitionScope.Project, definition.Scope);
        Assert.Equal("alpha", definition.Project);
        Assert.Equal(3, definition.VersionNumber);
        Assert.Equal(ForkedTask.Hash, definition.ContentHash);
        Assert.Equal(ParcelType.CanonicalJson(ForkedTask), definition.Content);
        Assert.Equal(new ForkBase("task", "software", "0.1.0", SoftwareTypes.UnownedTask.Hash), definition.ForkBase);
        Assert.Null(definition.ForkBase?.Version);
        Assert.Equal(DefinitionStatus.Draft, definition.Status);
    }

    /// <summary>Fork bases that each miss the one module-scope task type on exactly one of the four values, or name another kind's definition.</summary>
    public static TheoryData<string, string, string, string> UnmatchedForkBases() => new()
    {
        { "bug", "software", "0.1.0", SoftwareTypes.UnownedTask.Hash },
        { "task", "cms", "0.1.0", SoftwareTypes.UnownedTask.Hash },
        { "task", "software", "0.2.0", SoftwareTypes.UnownedTask.Hash },
        { "task", "software", "0.1.0", SoftwareTypes.Idea.Hash },
        { "build", "software", "0.1.0", SoftwareFlows.Build.Hash },
    };

    [Theory]
    [MemberData(nameof(UnmatchedForkBases))]
    public void A_fork_base_matching_no_module_scope_definition_of_the_drafts_kind_on_all_four_values_is_refused_naming_it(
        string name, string module, string moduleVersion, string contentHash)
    {
        Definition[] held =
        [
            ModuleVersions.Of(ModuleDefinition.FromParcelType(SoftwareTypes.UnownedTask)),
            ModuleVersions.Of(ModuleDefinition.FromParcelType(SoftwareTypes.Idea)),
            ModuleVersions.Of(ModuleDefinition.FromFlow(SoftwareFlows.Build)),
        ];
        var forkBase = new ForkBase(name, module, moduleVersion, contentHash);

        DomainException exception = Assert.Throws<DomainException>(() => DraftFork(forkBase, held));
        Assert.Equal(
            $"The fork base '{name}' of module '{module}' version {moduleVersion} with content hash '{contentHash}' of 'task' names no module-scope parcel type; a fork base names a module-scope definition of the draft's own kind, by name, module, module version, and content hash.",
            exception.Message);
    }

    [Fact]
    public void A_draft_whose_fork_base_records_a_store_given_version_number_or_lacks_a_value_is_refused()
    {
        Definition taskBase = ModuleVersions.Of(ModuleDefinition.FromParcelType(SoftwareTypes.UnownedTask));
        ForkBase[] invalid =
        [
            ForkBase.Earlier("task", 1),
            ForkBase.Of(taskBase) with { Version = 1 },
            ForkBase.Of(taskBase) with { Module = null },
            ForkBase.Of(taskBase) with { ModuleVersion = " " },
            ForkBase.Of(taskBase) with { ContentHash = "" },
        ];

        Assert.All(invalid, forkBase => Assert.StartsWith(
            "A fork base names the module-scope definition a customization came from by its name, module, module version, and content hash, and records no version number a store gave it; 'task' has an invalid one.",
            Assert.Throws<DomainException>(() => DraftFork(forkBase, [taskBase])).Message,
            StringComparison.Ordinal));
    }

    [Fact]
    public void A_fork_base_is_refused_when_no_module_versions_are_given_and_matched_by_name_without_case()
    {
        Definition taskBase = ModuleVersions.Of(ModuleDefinition.FromParcelType(SoftwareTypes.UnownedTask));
        ForkBase forkBase = ForkBase.Of(taskBase) with { Name = "Task" };

        DomainException exception = Assert.Throws<DomainException>(() => DraftFork(forkBase, null, scope: DefinitionScope.Owner));
        Assert.StartsWith("The fork base 'Task' of module 'software' version 0.1.0", exception.Message, StringComparison.Ordinal);

        Assert.Equal(forkBase, DraftFork(forkBase, [taskBase], scope: DefinitionScope.Owner).ForkBase);
    }

    [Fact]
    public void A_fork_base_names_the_definition_with_its_content_hash_among_two_of_one_name_module_and_module_version()
    {
        // As an upgraded store holds today: the task type before and after the repos field, both at 0.1.0.
        Definition before = ModuleVersions.Of(ModuleDefinition.FromParcelType(SoftwareTypes.UnownedTask));
        Definition after = ModuleVersions.Of(ModuleDefinition.FromParcelType(SoftwareTypes.Task), version: 2);
        ParcelType fork = SoftwareTypes.UnownedTask with { Stages = [.. SoftwareTypes.UnownedTask.Stages, "qa"] };

        Definition drafted = Definition.Create(DraftFork(ForkBase.Of(before), [after, before], fork));

        Assert.Equal(SoftwareTypes.UnownedTask.Hash, drafted.ForkBase?.ContentHash);
        Assert.Same(before, new[] { after, before }.Single(d => drafted.ForkBase?.IsBaseOf(d) == true));
    }

    [Fact]
    public void A_fork_that_keeps_a_fields_owner_keeps_its_kind_too()
    {
        ParcelType moduleTask = SoftwareTypes.Task;
        Definition taskBase = ModuleVersions.Of(ModuleDefinition.FromParcelType(moduleTask));
        FieldDeclaration repos = moduleTask.Fields.Single(f => f.Owner is not null);
        ParcelType keepsKind = moduleTask with { Stages = [.. moduleTask.Stages, "qa"] };
        ParcelType changesKind = keepsKind with
        {
            Fields = [.. keepsKind.Fields.Select(f => f.Name == repos.Name ? f with { Kind = FieldKinds.Text } : f)],
        };

        Assert.Equal(keepsKind.Hash, DraftFork(ForkBase.Of(taskBase), [taskBase], keepsKind).ContentHash);
        foreach (DefinitionScope scope in new[] { DefinitionScope.Project, DefinitionScope.Owner })
        {
            DomainException exception = Assert.Throws<DomainException>(() => DraftFork(ForkBase.Of(taskBase), [taskBase], changesKind, scope));
            Assert.Equal(
                $"Field '{repos.Name}' of parcel type 'task' keeps the module 'software' as its owner but declares the kind 'text', and its fork base {ForkBase.Of(taskBase).Describe()} declares that field of the kind '{repos.Kind}'; a type that keeps a field's owner from its fork base keeps the field's kind too.",
                exception.Message);
        }
    }

    [Fact]
    public void Content_round_trips_to_a_parcel_type_with_the_same_hash()
    {
        foreach (ParcelType type in SoftwareTypes.All)
        {
            Assert.Equal(type.Hash, ParcelType.FromJson(ParcelType.CanonicalJson(type)).Hash);
        }
    }

    [Fact]
    public void Apply_moves_a_definition_through_draft_published_retired()
    {
        Definition definition = Draft();
        definition.Apply(DefinitionDecider.Publish(definition, "owner", Now));
        Assert.Equal(DefinitionStatus.Published, definition.Status);
        definition.Apply(DefinitionDecider.Retire(definition, "owner", Now));
        Assert.Equal(DefinitionStatus.Retired, definition.Status);
    }

    [Fact]
    public void Publishing_a_definition_that_is_not_a_draft_is_refused()
    {
        Definition definition = Draft();
        definition.Apply(DefinitionDecider.Publish(definition, "owner", Now));

        DomainException exception = Assert.Throws<DomainException>(() => DefinitionDecider.Publish(definition, "owner", Now));
        Assert.Matches(@"(?s)^Only a draft can be published.*is published.*$", exception.Message);

        definition.Apply(DefinitionDecider.Retire(definition, "owner", Now));
        DomainException exception2 = Assert.Throws<DomainException>(() => DefinitionDecider.Publish(definition, "owner", Now));
        Assert.Matches(@"(?s)^Only a draft can be published.*is retired.*$", exception2.Message);
    }

    [Fact]
    public void Retiring_a_definition_that_is_not_published_is_refused()
    {
        Definition definition = Draft();
        DomainException exception = Assert.Throws<DomainException>(() => DefinitionDecider.Retire(definition, "owner", Now));
        Assert.Matches(@"(?s)^Only a published definition can be retired.*is a draft.*$", exception.Message);
    }

    [Fact]
    public void Retiring_a_module_scope_definition_is_refused()
    {
        (DefinitionDrafted drafted, DefinitionPublished published) =
            DefinitionDecider.Import(ModuleDefinition.FromParcelType(SoftwareTypes.UnownedTask), "software", "0.1.0", [], "import", Now)
            ?? throw new InvalidOperationException("Nothing was imported.");
        Definition imported = Definition.Create(drafted);
        imported.Apply(published);

        DomainException exception = Assert.Throws<DomainException>(() => DefinitionDecider.Retire(imported, "owner", Now));
        Assert.Equal(
            "A module-scope definition cannot be retired; 'task' version 1 came with module 'software' version 0.1.0.",
            exception.Message);
    }

    [Fact]
    public void Drafting_into_module_scope_is_refused()
    {
        DomainException exception = Assert.Throws<DomainException>(() => DefinitionDecider.Draft(
                DefinitionKind.ParcelType, "task", DefinitionScope.Module, null, 0,
                ParcelType.CanonicalJson(SoftwareTypes.UnownedTask), SoftwareTypes.UnownedTask.Hash, null, "brian", Now));
        Assert.StartsWith("A definition cannot be drafted into module scope", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Importing_produces_the_drafted_and_published_events_together_in_module_scope_with_the_module_and_its_version()
    {
        ModuleDefinition idea = ModuleDefinition.FromParcelType(SoftwareTypes.Idea);
        Definition earlier = ModuleVersions.Of(ModuleDefinition.FromParcelType(SoftwareTypes.Idea with { Resolutions = ["split"] }));

        (DefinitionDrafted drafted, DefinitionPublished published) =
            DefinitionDecider.Import(idea, "software", "0.2.0", [earlier], "import", Now) ?? throw new InvalidOperationException("Nothing was imported.");

        Assert.Equal(DefinitionScope.Module, drafted.Scope);
        Assert.Null(drafted.Project);
        Assert.Equal(2, drafted.Version);
        Assert.Equal("software", drafted.Module);
        Assert.Equal("0.2.0", drafted.ModuleVersion);
        Assert.Equal(SoftwareTypes.Idea.Hash, drafted.ContentHash);
        Assert.Equal(drafted.DefinitionId, published.DefinitionId);

        Definition definition = Definition.Create(drafted);
        definition.Apply(published);
        Assert.Equal(DefinitionStatus.Published, definition.Status);
    }

    [Fact]
    public void Importing_content_the_highest_version_has_records_nothing()
    {
        ModuleDefinition idea = ModuleDefinition.FromParcelType(SoftwareTypes.Idea);
        Definition[] held =
        [
            ModuleVersions.Of(ModuleDefinition.FromParcelType(SoftwareTypes.Idea with { Resolutions = ["split"] })),
            ModuleVersions.Of(idea, version: 2, moduleVersion: "0.2.0"),
        ];

        Assert.Null(DefinitionDecider.Import(idea, "software", "0.2.0", held, "import", Now));
    }

    [Fact]
    public void Importing_content_only_an_earlier_version_has_records_nothing()
    {
        ModuleDefinition idea = ModuleDefinition.FromParcelType(SoftwareTypes.Idea);
        Definition[] held =
        [
            ModuleVersions.Of(idea),
            ModuleVersions.Of(ModuleDefinition.FromParcelType(SoftwareTypes.Idea with { Resolutions = ["split"] }), version: 2, moduleVersion: "0.2.0"),
        ];

        Assert.Null(DefinitionDecider.Import(idea, "software", "0.3.0", held, "import", Now));
    }

    [Fact]
    public void A_parcel_type_whose_content_cannot_be_read_is_refused()
    {
        foreach (string content in new[] { "not json", "{}", "null", """{"Name":"task","Stages":null}""" })
        {
            DomainException exception = Assert.Throws<DomainException>(() => DefinitionDecider.Draft(
                    DefinitionKind.ParcelType, "task", DefinitionScope.Project, "alpha", 0, content, "abc", null, "brian", Now));
            Assert.Matches(@"(?s)^The content.*parcel type.*$", exception.Message);
        }
    }

    [Fact]
    public void A_parcel_type_that_fails_validation_is_refused()
    {
        ParcelType broken = SoftwareTypes.UnownedTask with { TerminalStage = "finished" };

        DomainException exception = Assert.Throws<DomainException>(() => DraftTask(broken));
        Assert.Contains("terminal stage", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_parcel_type_whose_content_hash_differs_is_refused()
    {
        DomainException exception = Assert.Throws<DomainException>(() => DefinitionDecider.Draft(
                DefinitionKind.ParcelType, "task", DefinitionScope.Project, "alpha", 0,
                ParcelType.CanonicalJson(SoftwareTypes.UnownedTask), SoftwareTypes.Idea.Hash, null, "brian", Now));
        Assert.StartsWith($"The content hash '{SoftwareTypes.Idea.Hash}' differs from the parcel type's hash", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_definition_named_for_a_different_type_is_refused()
    {
        DomainException exception = Assert.Throws<DomainException>(() => DefinitionDecider.Draft(
                DefinitionKind.ParcelType, "bug", DefinitionScope.Project, "alpha", 0,
                ParcelType.CanonicalJson(SoftwareTypes.UnownedTask), SoftwareTypes.UnownedTask.Hash, null, "brian", Now));
        Assert.StartsWith("A parcel-type definition is named for its type", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Scope_and_project_must_agree()
    {
        DomainException exception = Assert.Throws<DomainException>(() => DraftTask(project: " "));
        Assert.StartsWith("A project-scope definition belongs to a project", exception.Message, StringComparison.Ordinal);

        DomainException exception2 = Assert.Throws<DomainException>(() => DefinitionDecider.Draft(
                DefinitionKind.ParcelType, "task", DefinitionScope.Owner, "alpha", 0,
                ParcelType.CanonicalJson(SoftwareTypes.UnownedTask), SoftwareTypes.UnownedTask.Hash, null, "brian", Now));
        Assert.StartsWith("Only a project-scope definition belongs to a project", exception2.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Publish_and_retire_are_open_only_to_the_owner_and_draft_to_everyone()
    {
        var member = new Principal("pat", Role.Member);
        var agent = new Principal("build-session", Role.Agent);

        foreach (Principal principal in new[] { member, agent })
        {
            Assert.NotNull(CommandRegistry.Require("definition.draft", principal));
            DomainException exception = Assert.Throws<DomainException>(() => CommandRegistry.Require("definition.publish", principal));
            Assert.Equal($"'definition.publish' is not open to the {principal.Role} role.", exception.Message);
            DomainException exception2 = Assert.Throws<DomainException>(() => CommandRegistry.Require("definition.retire", principal));
            Assert.Equal($"'definition.retire' is not open to the {principal.Role} role.", exception2.Message);
        }

        Assert.NotNull(CommandRegistry.Require("definition.publish", Principal.LocalOwner));
        Assert.NotNull(CommandRegistry.Require("definition.retire", Principal.LocalOwner));
    }

    private static DefinitionDrafted DraftFlow(Flow flow, string? name = null, string? hash = null, IReadOnlySet<string>? systemSteps = null) =>
        DefinitionDecider.Draft(
            DefinitionKind.Flow, name ?? flow.Name, DefinitionScope.Project, "alpha", 0,
            Flow.CanonicalJson(flow), hash ?? flow.Hash, null, "brian", Now, knownSystemSteps: systemSteps ?? ShippedSystemSteps.Names);

    [Fact]
    public void A_valid_flow_is_drafted_with_its_hash_and_content_that_reads_back()
    {
        DefinitionDrafted drafted = DraftFlow(SoftwareFlows.Build);

        Assert.Equal(DefinitionKind.Flow, drafted.Kind);
        Assert.Equal(SoftwareFlows.Build.Hash, drafted.ContentHash);
        Assert.Equal(SoftwareFlows.Build.Hash, Flow.FromJson(drafted.Content).Hash);
    }

    [Fact]
    public void A_flow_that_fails_validation_is_refused_quoting_the_validation_message()
    {
        Flow broken = SoftwareFlows.Build with { Budget = 0 };

        DomainException exception = Assert.Throws<DomainException>(() => DraftFlow(broken));
        Assert.Equal("Flow 'build' needs a budget greater than zero; every flow declares one.", exception.Message);
    }

    [Fact]
    public void A_flow_naming_a_system_step_absent_from_the_names_passed_in_is_refused_even_when_a_catalog_holds_it()
    {
        var withoutGates = new HashSet<string>(ShippedSystemSteps.Names.Where(a => a != SoftwareSteps.Gates), StringComparer.Ordinal);
        var withoutMove = new HashSet<string>(ShippedSystemSteps.Names.Where(a => a != CoreSystemSteps.MoveParcel), StringComparer.Ordinal);

        DomainException exception = Assert.Throws<DomainException>(() => DraftFlow(SoftwareFlows.Build, systemSteps: withoutGates));
        Assert.Contains("names the system step 'software/gates', which is not a known system step", exception.Message, StringComparison.Ordinal);
        DomainException exception2 = Assert.Throws<DomainException>(() => DraftFlow(SoftwareFlows.Build, systemSteps: withoutMove));
        Assert.Contains("names the system step 'move-parcel', which is not a known system step", exception2.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flow_naming_a_system_step_among_the_names_passed_in_is_drafted_even_when_neither_catalog_holds_it()
    {
        FlowStep[] steps = [.. SoftwareFlows.Build.Steps.Select(s => s.SystemStep == SoftwareSteps.Gates ? s with { SystemStep = "teleport" } : s)];
        Flow teleporting = SoftwareFlows.Build with { Steps = steps };
        Assert.DoesNotContain("teleport", CoreSystemSteps.All.Concat(SoftwareSteps.All));

        DomainException exception = Assert.Throws<DomainException>(() => DraftFlow(teleporting));
        Assert.Contains("names the system step 'teleport', which is not a known system step", exception.Message, StringComparison.Ordinal);

        DefinitionDrafted drafted = DraftFlow(teleporting, systemSteps: new HashSet<string>([.. ShippedSystemSteps.Names, "teleport"], StringComparer.Ordinal));
        Assert.Equal(teleporting.Hash, drafted.ContentHash);
    }

    [Fact]
    public void Importing_a_flow_validates_it_against_the_names_passed_in()
    {
        ModuleDefinition build = ModuleDefinition.FromFlow(SoftwareFlows.Build);

        DomainException exception = Assert.Throws<DomainException>(() => DefinitionDecider.Import(build, "software", "0.1.0", [], "import", Now));
        Assert.Contains("which is not a known system step", exception.Message, StringComparison.Ordinal);
        Assert.Equal(SoftwareFlows.Build.Hash, DefinitionDecider.Import(build, "software", "0.1.0", [], "import", Now, knownSystemSteps: ShippedSystemSteps.Names)?.Drafted.ContentHash);
    }

    [Fact]
    public void A_flow_whose_content_hash_differs_from_its_content_is_refused()
    {
        DomainException exception = Assert.Throws<DomainException>(() => DraftFlow(SoftwareFlows.Build, hash: "0123456789abcdef"));
        Assert.StartsWith("The content hash '0123456789abcdef' differs from the flow's hash", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_flow_whose_content_cannot_be_read_is_refused()
    {
        string[] contents = ["not json", "null", "{\"Name\":\"build\",\"Steps\":[{\"Name\":\"s\",\"Condition\":\"x\"}]}"];
        foreach (string content in contents)
        {
            DomainException exception = Assert.Throws<DomainException>(() => DefinitionDecider.Draft(
                    DefinitionKind.Flow, "build", DefinitionScope.Project, "alpha", 0, content, "abc", null, "brian", Now));
            Assert.Matches(@"(?s)^The content.*flow.*$", exception.Message);
        }
    }

    [Fact]
    public void A_flow_definition_named_for_a_different_flow_is_refused()
    {
        DomainException exception = Assert.Throws<DomainException>(() => DraftFlow(SoftwareFlows.Build, name: "ship"));
        Assert.StartsWith("A flow definition is named for its flow", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Importing_a_modules_flow_produces_a_published_module_scope_definition()
    {
        (DefinitionDrafted drafted, DefinitionPublished published) =
            DefinitionDecider.Import(ModuleDefinition.FromFlow(SoftwareFlows.Discovery), "software", "0.1.0", [], "import", Now, knownSystemSteps: ShippedSystemSteps.Names)
            ?? throw new InvalidOperationException("Nothing was imported.");

        Assert.Equal(DefinitionKind.Flow, drafted.Kind);
        Assert.Equal(DefinitionScope.Module, drafted.Scope);
        Assert.Equal(SoftwareFlows.Discovery.Hash, drafted.ContentHash);
        Assert.Equal(drafted.DefinitionId, published.DefinitionId);
    }

    [Fact]
    public void A_drafted_parcel_type_is_validated_against_the_gate_names_it_is_given()
    {
        ParcelType gated = SoftwareTypes.UnownedTask with { Fields = [], GateBindings = [new GateBinding("refine", "build", "has-an-owner")] };
        string content = ParcelType.CanonicalJson(gated);

        DomainException exception = Assert.Throws<DomainException>(() => DefinitionDecider.Draft(
                DefinitionKind.ParcelType, "task", DefinitionScope.Project, "alpha", 0, content, gated.Hash, null, "brian", Now));
        Assert.Contains("binds the gate 'has-an-owner', which is not a gate this daemon runs", exception.Message, StringComparison.Ordinal);

        DefinitionDrafted drafted = DefinitionDecider.Draft(
            DefinitionKind.ParcelType, "task", DefinitionScope.Project, "alpha", 0, content, gated.Hash, null, "brian", Now,
            new HashSet<string>(StringComparer.Ordinal) { "has-an-owner" });
        Assert.Equal(gated.Hash, drafted.ContentHash);
    }
}
