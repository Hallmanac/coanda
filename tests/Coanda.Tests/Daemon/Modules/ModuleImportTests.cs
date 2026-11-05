using System.Text.Json.Nodes;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.Definitions;
using Coanda.Core.Definitions.Events;
using Coanda.Core.Flows;
using Coanda.Core.Registry;
using Coanda.Core.Types;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Store;
using Coanda.Tests.Core;
using Fisher;
using JasperFx.Events.Documents;

namespace Coanda.Tests.Daemon.Modules;

/// <summary>
/// At start the daemon reads each module's <c>module.json</c> and definitions folder and records every
/// definition as published in module scope with the module's name and version: an unchanged file
/// records nothing, an edited one its name's next version, and a module with a file that breaks a rule,
/// or a name another module already holds, records nothing and stops the start naming the file.
/// </summary>
public sealed class ModuleImportTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>
    /// Each definition the software module brings, with the content hash its C# definition had on main at
    /// e86428f, but for the task type, which has since gained the repos field the module owns and marked
    /// it as the type's tag field.
    /// </summary>
    public static TheoryData<DefinitionKind, string, string> HashesOnMain() => new()
    {
        { DefinitionKind.ParcelType, "idea", "e6c4447a6bc6586d" },
        { DefinitionKind.ParcelType, "task", "3e16509d2543dbb9" },
        { DefinitionKind.Flow, "build", "728a81e81272ed5e" },
        { DefinitionKind.Flow, "merge", "3ea9bf7b75a6e9f4" },
        { DefinitionKind.Flow, "discovery", "c8b01e99a6cc7916" },
        { DefinitionKind.Flow, "review-on-assignment", "fabaabb2eb82ff10" },
        { DefinitionKind.Agent, "build", "37dd84328f49ca25" },
        { DefinitionKind.Agent, "reviewer-adversarial", "4ff7ac20b180df0d" },
        { DefinitionKind.Agent, "reviewer-conformance", "9f867144783e855c" },
        { DefinitionKind.Agent, "fix", "4ca4afe60ac4e100" },
        { DefinitionKind.Agent, "discovery", "e99e12f13d399792" },
    };

    private static async Task StartAndStopAsync(NodeHome home, DaemonOptions? options = null)
    {
        await using RunningDaemon daemon = await Daemons.StartAsync(home, options ?? new DaemonOptions(), None);
        await daemon.StopAsync(None);
    }

    private static async Task<List<Definition>> InNodeAsync(NodeHome home)
    {
        await using IDocumentStore store = StoreConfiguration.Open(home.DatabasePath);
        await using IQuerySession session = store.QuerySession(Tenants.Node);
        return [.. await session.Query<Definition>().ToListAsync(None)];
    }

    /// <summary>A copy of the software module's directory as the build left it beside the daemon, for a test to edit.</summary>
    private static string CopyOfSoftware(TemporaryHome temp)
    {
        string copy = Path.Combine(temp.Home.Path, "software-module");
        foreach (string file in Directory.EnumerateFiles(DaemonOptions.DefaultSoftwareModulePath, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(copy, Path.GetRelativePath(DaemonOptions.DefaultSoftwareModulePath, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target) ?? copy);
            File.Copy(file, target);
        }

        return copy;
    }

    private static ParcelType Article(params FieldDeclaration[] fields) => SoftwareTypes.UnownedTask with { Name = "article", FlowBindings = [], Fields = [.. SoftwareTypes.UnownedTask.Fields, .. fields] };

    [Theory]
    [MemberData(nameof(HashesOnMain))]
    public async Task Each_imported_definition_carries_the_hash_its_csharp_definition_had_on_main_or_the_task_has_now(DefinitionKind kind, string name, string hash)
    {
        using var temp = new TemporaryHome();
        await StartAndStopAsync(temp.Home);

        Definition imported = Assert.Single(await InNodeAsync(temp.Home), d => d.Kind == kind && d.Name == name);

        Assert.Equal(hash, imported.ContentHash);
        Assert.Equal(
            (DefinitionScope.Module, DefinitionStatus.Published, 1, SoftwareDefinitions.Manifest.Name, SoftwareDefinitions.Manifest.Version),
            (imported.Scope, imported.Status, imported.VersionNumber, imported.Module, imported.ModuleVersion));
    }

    [Fact]
    public async Task The_software_module_brings_exactly_the_eleven_definitions_and_nothing_else_is_imported()
    {
        using var temp = new TemporaryHome();
        await StartAndStopAsync(temp.Home);

        Assert.Equivalent(
            HashesOnMain().Select(row => ((DefinitionKind)row[0], (string)row[1], (string)row[2])),
            (await InNodeAsync(temp.Home)).Select(d => (d.Kind, d.Name, d.ContentHash)),
            strict: true);
    }

    [Fact]
    public void Neither_the_scope_nor_the_module_nor_its_version_is_part_of_a_content_hash()
    {
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        // The task's repos field names the software module as its owner, which only that module's own
        // scope may, so the task is drafted elsewhere without it.
        foreach (ModuleDefinition definition in SoftwareDefinitions.All.Select(d =>
            d is { Kind: DefinitionKind.ParcelType, Name: "task" } ? ModuleDefinition.FromParcelType(SoftwareTypes.UnownedTask) : d))
        {
            DefinitionDrafted? first = DefinitionDecider.Import(definition, "software", "0.1.0", [], "import", now, knownSystemSteps: ShippedSystemSteps.Names)?.Drafted;
            DefinitionDrafted? second = DefinitionDecider.Import(definition, "elsewhere", "7.0.0", [], "import", now, knownSystemSteps: ShippedSystemSteps.Names)?.Drafted;
            DefinitionDrafted owned = DefinitionDecider.Draft(
                definition.Kind, definition.Name, DefinitionScope.Owner, null, 0, definition.Content, definition.ContentHash, null, "brian", now,
                knownSystemSteps: ShippedSystemSteps.Names);

            Assert.Equal(definition.ContentHash, first?.ContentHash);
            Assert.Equal(definition.ContentHash, second?.ContentHash);
            Assert.Equal(definition.ContentHash, owned.ContentHash);
            Assert.Equal(definition.Content, second?.Content);
            Assert.DoesNotContain("0.1.0", definition.Content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Editing_a_definition_file_imports_it_as_its_names_next_version_at_the_next_start_with_no_change_to_the_manifest()
    {
        using var temp = new TemporaryHome();
        string software = CopyOfSoftware(temp);
        var options = new DaemonOptions { SoftwareModulePath = software };
        await StartAndStopAsync(temp.Home, options);
        string file = Path.Combine(software, "definitions", "flows", "review-on-assignment.json");
        JsonObject edited = JsonNode.Parse(await File.ReadAllTextAsync(file, None))?.AsObject() ?? throw new InvalidOperationException("Not an object.");
        edited["Budget"] = 4;
        await File.WriteAllTextAsync(file, edited.ToJsonString(), None);

        await StartAndStopAsync(temp.Home, options);

        List<Definition> held = await InNodeAsync(temp.Home);
        Assert.Equal(12, held.Count);
        Definition next = Assert.Single(held, d => d.Name == "review-on-assignment" && d.VersionNumber == 2);
        Assert.Equal((SoftwareFlows.ReviewOnAssignment with { Budget = 4m }).Hash, next.ContentHash);
        Assert.Equal(("software", SoftwareDefinitions.Manifest.Version), (next.Module, next.ModuleVersion));
        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        Assert.Equal(next.ContentHash, (await new DefinitionResolver(store).ResolveAsync("alpha", DefinitionKind.Flow, "review-on-assignment", None))?.ContentHash);
    }

    [Fact]
    public async Task Reverting_a_definition_file_to_an_earlier_versions_content_records_nothing_and_the_newest_version_keeps_governing()
    {
        using var temp = new TemporaryHome();
        string software = CopyOfSoftware(temp);
        var options = new DaemonOptions { SoftwareModulePath = software };
        string file = Path.Combine(software, "definitions", "flows", "review-on-assignment.json");
        string original = await File.ReadAllTextAsync(file, None);
        await StartAndStopAsync(temp.Home, options);
        JsonObject edited = JsonNode.Parse(original)?.AsObject() ?? throw new InvalidOperationException("Not an object.");
        edited["Budget"] = 4;
        await File.WriteAllTextAsync(file, edited.ToJsonString(), None);
        await StartAndStopAsync(temp.Home, options);
        await File.WriteAllTextAsync(file, original, None);

        await StartAndStopAsync(temp.Home, options);

        List<Definition> held = await InNodeAsync(temp.Home);
        Assert.Equal(12, held.Count);
        Assert.DoesNotContain(held, d => d.Name == "review-on-assignment" && d.VersionNumber == 3);
        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        Definition? governing = await new DefinitionResolver(store).ResolveAsync("alpha", DefinitionKind.Flow, "review-on-assignment", None);
        Assert.Equal((2, (SoftwareFlows.ReviewOnAssignment with { Budget = 4m }).Hash), (governing?.VersionNumber, governing?.ContentHash));
    }

    [Theory]
    [InlineData("module.json")]
    [InlineData("definitions")]
    public async Task A_missing_manifest_or_definitions_folder_stops_the_start_naming_the_path(string missing)
    {
        using var temp = new TemporaryHome();
        string software = CopyOfSoftware(temp);
        string path = Path.Combine(software, missing);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        else
        {
            Directory.Delete(path, recursive: true);
        }

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => StartAndStopAsync(temp.Home, new DaemonOptions { SoftwareModulePath = software }));

        Assert.Contains($"'{path}'", exception.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(temp.Home.DaemonInfoPath));
    }

    [Fact]
    public async Task A_software_manifest_naming_another_module_stops_the_start()
    {
        using var temp = new TemporaryHome();
        string software = CopyOfSoftware(temp);
        string manifest = Path.Combine(software, "module.json");
        await File.WriteAllTextAsync(manifest, (await File.ReadAllTextAsync(manifest, None)).Replace("\"software\"", "\"engineering\"", StringComparison.Ordinal), None);

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => StartAndStopAsync(temp.Home, new DaemonOptions { SoftwareModulePath = software }));

        Assert.StartsWith($"The software module's manifest in '{software}' names the module 'engineering'; it is 'software',", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>A file of a stand-in module that breaks a rule, as its path in the definitions folder, its text, and the rule's wording.</summary>
    public static TheoryData<string, string, string> BrokenFiles() => new()
    {
        { "flows/publish.json", Flow.CanonicalJson(new Flow
            {
                Name = "publish", Accepts = ["article"], Outcome = "published", Budget = 1m, SpawnCap = 1,
                Steps = [new FlowStep { Name = "publish", SystemStep = "cms/publish", Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of("done"), VisitCap = 1, Target = Flow.End }] }],
            }), "names the system step 'cms/publish', which is not a known system step" },
        { "types/review.json", ParcelType.CanonicalJson(Article() with { Name = "review", GateBindings = [new GateBinding("refine", "build", "has-an-editor")] }), "which is not a gate this daemon runs" },
        { "agents/editor.json", "{ not json", "The content cannot be read as an agent" },
        { "agents/writer.json", AgentDefinition.CanonicalJson(SoftwareAgents.Build with { Name = "editor" }), "a definition file is named for the definition it holds, 'editor.json'" },
        { "triggers/review-requested.json", "{}", "is not a definition file" },
    };

    [Theory]
    [MemberData(nameof(BrokenFiles))]
    public async Task A_module_with_a_file_that_breaks_a_rule_records_nothing_and_stops_the_start_naming_the_file_and_the_rule(string file, string text, string rule)
    {
        using var temp = new TemporaryHome();
        using StandInModuleDirectory cms = StandInModuleDirectory.Write("cms", "1.0.0", ModuleDefinition.FromParcelType(Article()));
        string path = cms.WriteFile(file, text);
        string named = file.StartsWith("triggers/", StringComparison.Ordinal) ? Path.GetDirectoryName(path) ?? path : path;

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => StartAndStopAsync(temp.Home, new DaemonOptions { ModulePaths = [cms.Path] }));

        Assert.Contains($"'{named}'", exception.Message, StringComparison.Ordinal);
        Assert.Contains(rule, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(await InNodeAsync(temp.Home), d => d.Module == "cms" || d.Name == "article");
        Assert.False(File.Exists(temp.Home.DaemonInfoPath));
    }

    [Fact]
    public async Task A_modules_type_is_checked_against_the_gates_the_daemon_runs_as_a_draft_of_no_project_is()
    {
        using var temp = new TemporaryHome();
        ParcelType gated = Article() with { GateBindings = [.. SoftwareTypes.UnownedTask.GateBindings, new GateBinding("build", "review", StandInGate.DefaultName)] };
        using StandInModuleDirectory cms = StandInModuleDirectory.Write("cms", "1.0.0", ModuleDefinition.FromParcelType(gated));
        string file = Path.Combine(cms.DefinitionsPath, "types", "article.json");

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => StartAndStopAsync(temp.Home, new DaemonOptions { ModulePaths = [cms.Path] }));
        Assert.Contains($"'{file}' breaks a rule: Parcel type 'article' binds the gate '{StandInGate.DefaultName}', which is not a gate this daemon runs", exception.Message, StringComparison.Ordinal);

        await StartAndStopAsync(temp.Home, new DaemonOptions { ModulePaths = [cms.Path], Gates = [new StandInGate()] });
        Assert.Equal(gated.Hash, Assert.Single(await InNodeAsync(temp.Home), d => d.Module == "cms").ContentHash);
    }

    [Fact]
    public async Task An_entry_of_the_definitions_folder_whose_name_starts_with_a_dot_is_passed_over()
    {
        using var temp = new TemporaryHome();
        using StandInModuleDirectory cms = StandInModuleDirectory.Write("cms", "1.0.0", ModuleDefinition.FromParcelType(Article()));
        cms.WriteFile(".DS_Store", "not a definition");
        cms.WriteFile("types/.DS_Store", "not a definition");

        await StartAndStopAsync(temp.Home, new DaemonOptions { ModulePaths = [cms.Path] });

        Assert.Equal("article", Assert.Single(await InNodeAsync(temp.Home), d => d.Module == "cms").Name);
    }

    [Fact]
    public async Task A_definition_whose_kind_and_name_another_module_holds_is_refused_and_its_module_records_nothing()
    {
        using var temp = new TemporaryHome();
        using StandInModuleDirectory cms = StandInModuleDirectory.Write(
            "cms", "1.0.0", ModuleDefinition.FromParcelType(Article()), ModuleDefinition.FromParcelType(SoftwareTypes.UnownedTask));

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => StartAndStopAsync(temp.Home, new DaemonOptions { ModulePaths = [cms.Path] }));

        Assert.Equal(
            $"Module 'cms' version 1.0.0 cannot be imported: the definition file '{Path.Combine(cms.DefinitionsPath, "types", "task.json")}' breaks a rule: Module 'cms' brings the parcel type 'task', which module 'software' already brought; a definition's name is held by one module in module scope.",
            exception.Message);
        Assert.DoesNotContain(await InNodeAsync(temp.Home), d => d.Module == "cms" || d.Name == "article");
    }

    [Fact]
    public async Task A_stand_in_module_is_imported_beside_the_software_module_and_a_second_start_records_nothing()
    {
        using var temp = new TemporaryHome();
        using StandInModuleDirectory cms = StandInModuleDirectory.Write(
            "cms", "1.0.0", ModuleDefinition.FromParcelType(Article(new FieldDeclaration { Name = "slug", Kind = FieldKinds.Text, Owner = "cms" })));
        var options = new DaemonOptions { ModulePaths = [cms.Path] };

        await StartAndStopAsync(temp.Home, options);
        await StartAndStopAsync(temp.Home, options);

        List<Definition> held = await InNodeAsync(temp.Home);
        Definition article = Assert.Single(held, d => d.Module == "cms");
        Assert.Equal(("article", 1, "1.0.0", DefinitionScope.Module), (article.Name, article.VersionNumber, article.ModuleVersion, article.Scope));
        Assert.Equal(12, held.Count);
    }

    [Fact]
    public async Task A_modules_type_naming_another_module_as_a_fields_owner_is_refused_at_import()
    {
        using var temp = new TemporaryHome();
        using StandInModuleDirectory cms = StandInModuleDirectory.Write(
            "cms", "1.0.0", ModuleDefinition.FromParcelType(Article(new FieldDeclaration { Name = "repos", Kind = FieldKinds.TextList, Owner = "software" })));

        DomainException exception = await Assert.ThrowsAsync<DomainException>(() => StartAndStopAsync(temp.Home, new DaemonOptions { ModulePaths = [cms.Path] }));

        Assert.EndsWith(
            "breaks a rule: Field 'repos' of parcel type 'article' of module 'cms' names the module 'software' as its owner; a module's type names only its own module as a field's owner.",
            exception.Message);
        Assert.Contains(Path.Combine(cms.DefinitionsPath, "types", "article.json"), exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(await InNodeAsync(temp.Home), d => d.Module == "cms");
    }

    [Fact]
    public async Task A_project_fork_of_the_build_flow_reads_the_module_and_version_it_came_from_from_its_base()
    {
        using var temp = new TemporaryHome();
        await StartAndStopAsync(temp.Home);
        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        var definitions = new DefinitionService(store, TimeProvider.System, systemSteps: ShippedSystemSteps.Names);
        Flow fork = SoftwareFlows.Build with { Budget = 40m };

        Definition drafted = await definitions.DraftAsync(
            Principal.LocalOwner, DefinitionKind.Flow, DefinitionScope.Project, "alpha", fork.Name, Flow.CanonicalJson(fork), fork.Hash, new ForkBase("build", "software", SoftwareDefinitions.Manifest.Version, SoftwareFlows.Build.Hash), None);

        Definition forkedFrom = await new DefinitionResolver(store).ForkedFromAsync(drafted, None) ?? throw new InvalidOperationException("The fork's base resolves to nothing.");
        Assert.Equal((DefinitionScope.Module, "build", 1, SoftwareFlows.Build.Hash), (forkedFrom.Scope, forkedFrom.Name, forkedFrom.VersionNumber, forkedFrom.ContentHash));
        Assert.Equal(("software", SoftwareDefinitions.Manifest.Version), (forkedFrom.Module, forkedFrom.ModuleVersion));
        Assert.Equal((null, null), (drafted.Module, drafted.ModuleVersion));
    }

    /// <summary>The stand-in cms module's article type, owning its slug field as text, as a fork base names it.</summary>
    private static readonly ForkBase ArticleBase = new("article", "cms", "1.0.0", Article(new FieldDeclaration { Name = "slug", Kind = FieldKinds.Text, Owner = "cms" }).Hash);

    /// <summary>Owned fields in a user's type: only a field its fork base declares owned by that same module, of the kind it declares.</summary>
    public static TheoryData<string, string, ForkBase?, string?> UserOwners() => new()
    {
        // The fork keeps its base's owned field and its kind: recorded.
        { "slug", FieldKinds.Text, ArticleBase, null },
        // The fork names a different owner for that field.
        { "slug", FieldKinds.Text, ArticleBase, $"Field 'slug' of parcel type 'article' names the module 'software' as its owner; a type of project or owner scope names a field's owner only for a field its fork base declares owned by that same module, and its fork base {ArticleBase.Describe()} declares no field 'slug' owned by 'software'." },
        // The fork names an owner for a field its base does not own.
        { "summary", FieldKinds.Text, ArticleBase, $"Field 'summary' of parcel type 'article' names the module 'cms' as its owner; a type of project or owner scope names a field's owner only for a field its fork base declares owned by that same module, and its fork base {ArticleBase.Describe()} declares no field 'summary' owned by 'cms'." },
        // The fork keeps the field's owner but changes its kind.
        { "slug", FieldKinds.TextList, ArticleBase, $"Field 'slug' of parcel type 'article' keeps the module 'cms' as its owner but declares the kind 'text-list', and its fork base {ArticleBase.Describe()} declares that field of the kind 'text'; a type that keeps a field's owner from its fork base keeps the field's kind too." },
        // A type with no fork base names an owner.
        { "slug", FieldKinds.Text, null, "Field 'slug' of parcel type 'article' names the module 'cms' as its owner; a type of project or owner scope names a field's owner only for a field its fork base declares owned by that same module, and 'article' has no fork base." },
    };

    [Theory]
    [MemberData(nameof(UserOwners))]
    public async Task A_users_type_names_a_fields_owner_only_as_its_fork_base_declares_it(string field, string kind, ForkBase? forkBase, string? refusal)
    {
        using var temp = new TemporaryHome();
        using StandInModuleDirectory cms = StandInModuleDirectory.Write(
            "cms", "1.0.0", ModuleDefinition.FromParcelType(Article(new FieldDeclaration { Name = "slug", Kind = FieldKinds.Text, Owner = "cms" })));
        await StartAndStopAsync(temp.Home, new DaemonOptions { ModulePaths = [cms.Path] });
        string owner = refusal?.Contains("module 'software'", StringComparison.Ordinal) == true ? "software" : "cms";
        ParcelType article = Article(new FieldDeclaration { Name = field, Kind = kind, Owner = owner }) with { Resolutions = [.. SoftwareTypes.UnownedTask.Resolutions, "spiked"] };
        await using IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath);
        var definitions = new DefinitionService(store, TimeProvider.System, systemSteps: ShippedSystemSteps.Names);

        foreach ((DefinitionScope scope, string? project) in new[] { (DefinitionScope.Project, "alpha"), (DefinitionScope.Owner, (string?)null) })
        {
            Task<Definition> draft = definitions.DraftParcelTypeAsync(Principal.LocalOwner, scope, project, article, forkBase, None);
            if (refusal is null)
            {
                Definition recorded = await draft;
                Assert.Equal(owner, ParcelType.FromJson(recorded.Content).Field(field)?.Owner);
                continue;
            }

            DomainException exception = await Assert.ThrowsAsync<DomainException>(() => draft);
            Assert.Equal(refusal, exception.Message);
        }

        if (refusal is not null)
        {
            await using IQuerySession alpha = store.QuerySession("alpha");
            Assert.Empty(await alpha.Query<Definition>().ToListAsync(None));
            Assert.DoesNotContain(await InNodeAsync(temp.Home), d => d.Scope == DefinitionScope.Owner);
        }
    }
}
