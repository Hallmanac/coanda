using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Definitions.Events;
using Coanda.Core.Flows;
using Coanda.Core.Registry;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Definitions;
using Coanda.Daemon.Projects;
using Coanda.Daemon.Store;
using Coanda.Daemon.SystemSteps;
using Coanda.Modules.Software;
using Coanda.Sdk.Contract;
using Coanda.Sdk.Hosting;
using Coanda.Tests.Daemon.FlowRuns;
using Fisher;
using JasperFx.Events.Documents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Coanda.Tests.Daemon.Modules;

/// <summary>
/// A flow that gives a module's step arguments the step's argument schema refuses is refused when it is
/// drafted or imported, naming the step and the argument; a flow version already stored still runs, and
/// the start warns of it. Every schema a describe answer supplies is read from its own text alone.
/// </summary>
public sealed class StepArgumentSchemaTests
{
    private const string Project = "acme";
    private static readonly CancellationToken None = CancellationToken.None;

    private static Task<RunningDaemon> StartAsync(TemporaryHome temp, StepModule module, RecordingLogger? log = null, params string[] modulePaths) =>
        Daemons.StartAsync(
            temp.Home,
            new DaemonOptions
            {
                Modules = new Dictionary<string, IModule> { [StepModule.Name] = module },
                ModulePaths = modulePaths,
                LoggerProvider = log,
            },
            None);

    /// <summary>Registers <see cref="Project"/> with the software module and the stand-in switched on.</summary>
    private static async Task RegisterAsync(RunningDaemon daemon)
    {
        await RegisteredProjects.AddAsync(daemon, Project, None);
        await daemon.Services.GetRequiredService<ProjectService>()
            .SetModulesAsync(Principal.LocalOwner, Project, [SoftwareSteps.Module, StepModule.Name], None);
    }

    private static Flow OneStep(string name, string systemStep, params (string Name, JsonElement Value)[] arguments) => new()
    {
        Name = name,
        Accepts = ["task"],
        Outcome = "acted",
        Budget = 1m,
        SpawnCap = 1,
        Steps =
        [
            new FlowStep
            {
                Name = "act",
                SystemStep = systemStep,
                Arguments = arguments.ToDictionary(a => a.Name, a => a.Value),
                Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of("done"), VisitCap = 1, Target = Flow.End }],
            },
        ],
    };

    private static (string, JsonElement) Argument(string name, object value) => (name, JsonSerializer.SerializeToElement(value));

    public static TheoryData<string, string, string> Refused() => new()
    {
        { "missing", """{}""", "argument 'channel' is required and not given (PropertyRequired)" },
        { "a number", """{ "channel": 3 }""", "argument 'channel' must be a string (StringExpected)" },
        { "beside one the schema does not declare", """{ "channel": "news", "audience": "all" }""", "argument 'audience' is not one the schema declares (NoAdditionalPropertiesAllowed)" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_project_draft_whose_arguments_the_modules_step_schema_refuses_is_refused_naming_the_flow_the_step_and_the_argument(
        string what, string arguments, string complaint)
    {
        Assert.False(string.IsNullOrEmpty(what));
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await StartAsync(temp, new StepModule());
        await RegisterAsync(daemon);
        Dictionary<string, JsonElement> given = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments) ?? [];
        Flow flow = OneStep("publishing", StepModule.Step, [.. given.Select(a => (a.Key, a.Value))]);

        DomainException refused = await Assert.ThrowsAsync<DomainException>(() => Publishing.FlowAsync(daemon, Project, flow));

        Assert.Equal(
            $"Step 'act' of flow 'publishing' gives the system step 'cms/publish' arguments its argument schema refuses: {complaint}.",
            refused.Message);
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task The_same_flow_is_accepted_with_the_argument_given_as_a_string_in_a_project_and_in_no_project()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await StartAsync(temp, new StepModule());
        await RegisterAsync(daemon);
        Flow flow = OneStep("publishing", StepModule.Step, Argument("channel", "news"));

        await Publishing.FlowAsync(daemon, Project, flow);
        await DraftOwnerAsync(daemon, flow);
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_draft_of_no_project_is_checked_against_the_schema_of_a_module_the_node_hosts()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await StartAsync(temp, new StepModule());

        DomainException refused = await Assert.ThrowsAsync<DomainException>(() => DraftOwnerAsync(daemon, OneStep("publishing", StepModule.Step)));

        Assert.Equal(
            "Step 'act' of flow 'publishing' gives the system step 'cms/publish' arguments its argument schema refuses: argument 'channel' is required and not given (PropertyRequired).",
            refused.Message);
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_step_whose_describe_entry_declares_no_argument_schema_takes_any_scalar_arguments()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await StartAsync(temp, new StepModule(default));
        await RegisterAsync(daemon);

        await Publishing.FlowAsync(daemon, Project, OneStep("publishing", StepModule.Step, Argument("channel", 3), Argument("audience", "all")));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_timer_step_is_checked_as_on_main_and_an_argument_it_does_not_read_is_still_accepted()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await StartAsync(temp, new StepModule());
        await RegisterAsync(daemon);

        await Publishing.FlowAsync(daemon, Project, OneStep("waiting", CoreSystemSteps.Timer, Argument("seconds", 0), Argument("note", "unread")));
        await Publishing.FlowAsync(daemon, Project, OneStep("unset", CoreSystemSteps.Timer));
        DomainException refused = await Assert.ThrowsAsync<DomainException>(() =>
            Publishing.FlowAsync(daemon, Project, OneStep("nested", CoreSystemSteps.Timer, ("seconds", StepModule.Json("""{ "value": 1 }""")))));

        Assert.Equal("Argument 'seconds' of step 'act' in flow 'nested' must be a JSON string, number, or boolean.", refused.Message);
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_module_flow_whose_arguments_the_schema_refuses_records_nothing_at_import_and_stops_the_start_naming_the_file_the_step_and_the_argument()
    {
        using var temp = new TemporaryHome();
        using StandInModuleDirectory cms = StandInModuleDirectory.Write(
            StepModule.Name, "1.0.0", ModuleDefinition.FromFlow(OneStep("publishing", StepModule.Step, Argument("channel", 3))));
        string file = Path.Combine(cms.DefinitionsPath, "flows", "publishing.json");

        DomainException refused = await Assert.ThrowsAsync<DomainException>(() => StartAsync(temp, new StepModule(), modulePaths: cms.Path));

        Assert.Equal(
            $"Module 'cms' version 1.0.0 cannot be imported: the definition file '{file}' breaks a rule: "
            + "Step 'act' of flow 'publishing' gives the system step 'cms/publish' arguments its argument schema refuses: argument 'channel' must be a string (StringExpected).",
            refused.Message);
        Assert.DoesNotContain(await InNodeAsync(temp.Home), d => d.Module == StepModule.Name);
        Assert.False(File.Exists(temp.Home.DaemonInfoPath));

        cms.Write(ModuleDefinition.FromFlow(OneStep("publishing", StepModule.Step, Argument("channel", "news"))));
        await using RunningDaemon daemon = await StartAsync(temp, new StepModule(), modulePaths: cms.Path);
        await daemon.StopAsync(None);
        Assert.Single(await InNodeAsync(temp.Home), d => d.Module == StepModule.Name);
    }

    [Fact]
    public async Task Every_flow_the_software_module_ships_passes_its_steps_argument_schemas_at_import()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        FlowSystemSteps systemSteps = daemon.Services.GetRequiredService<FlowSystemSteps>();
        List<Definition> shipped = [.. (await InNodeAsync(temp.Home)).Where(d => d.Kind == DefinitionKind.Flow && d.Module == SoftwareSteps.Module)];
        List<FlowStep> moduleSteps = [.. shipped.SelectMany(d => Flow.FromJson(d.Content).Steps).Where(s => s.SystemStep?.StartsWith("software/", StringComparison.Ordinal) == true)];
        StepArgumentCheck check = await systemSteps.ArgumentCheckAsync(null, None);

        Assert.Equal(SoftwareDefinitions.All.Count(d => d.Kind == DefinitionKind.Flow), shipped.Count);
        Assert.NotEmpty(moduleSteps);
        Assert.All(moduleSteps, s => Assert.Empty(check(s.SystemStep ?? string.Empty, s.Arguments)));
        // The software steps' schemas are live: an argument none of them declares is refused.
        Assert.All(moduleSteps, s => Assert.NotEmpty(check(s.SystemStep ?? string.Empty, new Dictionary<string, JsonElement> { ["extra"] = JsonSerializer.SerializeToElement("x") })));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_flow_version_stored_before_the_check_still_runs_through_the_step_and_the_start_warns_of_it()
    {
        using var temp = new TemporaryHome();
        var module = new StepModule();
        await using (RunningDaemon first = await StartAsync(temp, module))
        {
            await RegisterAsync(first);
            await first.StopAsync(None);
        }

        // What a build without the check could have recorded: versions whose arguments the schemas refuse.
        Flow stored = OneStep("publishing", StepModule.Step, Argument("channel", 3));
        await using (IDocumentStore store = StoreConfiguration.Open(temp.Home.DatabasePath))
        {
            await RecordAsync(store, Tenants.RequireProject(Project), DefinitionScope.Project, Project, stored);
            await RecordAsync(store, Tenants.Node, DefinitionScope.Owner, null, OneStep("gating", "gates", Argument("strict", true)));
            await RecordAsync(store, Tenants.Node, DefinitionScope.Owner, null, OneStep("posting", "blog/post", Argument("channel", 3)));
            await RecordAsync(store, Tenants.Node, DefinitionScope.Owner, null, OneStep("retired", StepModule.Step), retired: true);
        }

        var log = new RecordingLogger();
        await using RunningDaemon daemon = await StartAsync(temp, module, log);

        List<string> warnings = [.. log.Entries.Where(e => e.Level == LogLevel.Warning && e.Message.Contains("argument schema", StringComparison.Ordinal)).Select(e => e.Message)];
        Assert.Equal(
            [
                "Flow 'gating' version 1 (owner scope, project none) gives step 'act' arguments the argument schema of the system step 'gates' refuses: argument 'strict' is not one the schema declares (NoAdditionalPropertiesAllowed). The version still runs as it did.",
                "Flow 'publishing' version 1 (project scope, project acme) gives step 'act' arguments the argument schema of the system step 'cms/publish' refuses: argument 'channel' must be a string (StringExpected). The version still runs as it did.",
            ],
            warnings.Order(StringComparer.Ordinal));

        using var client = new DaemonClient(temp.Home);
        ParcelView parcel = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, "task", "Publish it", null), None);
        FlowRunView started = await FlowRunDriver.StartAsync(client, Project, parcel.Id, stored.Name);
        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, started.Id, "ended");

        DispatchRequest dispatched = Assert.Single(module.Dispatches);
        Assert.Equal(ended.Id, dispatched.FlowRunId);
        Assert.Equal(3, dispatched.Arguments["channel"].GetInt32());
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_argument_schema_referring_to_a_url_stops_the_start_naming_the_module_the_step_and_the_reference_and_nothing_is_fetched()
    {
        using var temp = new TemporaryHome();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            string url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/channel.schema.json";
            var module = new StepModule(StepModule.Json($$"""{ "type": "object", "properties": { "channel": { "$ref": "{{url}}" } } }"""));

            DomainException refused = await Assert.ThrowsAsync<DomainException>(() => StartAsync(temp, module));

            Assert.Equal(
                $"The argument schema of step 'cms/publish' of module 'cms' refers to '{url}', outside itself; an argument schema refers only to its own definitions.",
                refused.Message);
            Assert.False(listener.Pending());
            Assert.False(File.Exists(temp.Home.DaemonInfoPath));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task A_settings_schema_referring_to_a_file_stops_the_start_naming_the_module_the_setting_and_the_reference()
    {
        using var temp = new TemporaryHome();
        string file = Path.Combine(temp.Home.Path, "site.schema.json");
        await File.WriteAllTextAsync(file, """{ "type": "string" }""", None);
        var settings = new SettingsSchema([new ReplicatedSetting("site", StepModule.Json($$"""{ "$ref": {{JsonSerializer.Serialize(file)}} }"""), Required: false)]);

        DomainException refused = await Assert.ThrowsAsync<DomainException>(() => StartAsync(temp, new StepModule(StepModule.ChannelSchema, settings)));

        Assert.Equal(
            $"The schema of setting 'site' in the settings schema of module 'cms' refers to '{file}', outside itself; a setting's schema refers only to its own definitions.",
            refused.Message);
    }

    private static Task<Definition> DraftOwnerAsync(RunningDaemon daemon, Flow flow) =>
        daemon.Services.GetRequiredService<DefinitionService>().DraftAsync(
            Principal.LocalOwner, DefinitionKind.Flow, DefinitionScope.Owner, null, flow.Name, Flow.CanonicalJson(flow), flow.Hash, null, None);

    private static async Task RecordAsync(IDocumentStore store, string tenant, DefinitionScope scope, string? project, Flow flow, bool retired = false)
    {
        Guid id = Ids.New();
        DateTimeOffset at = DateTimeOffset.UnixEpoch;
        var drafted = new DefinitionDrafted(id, DefinitionKind.Flow, flow.Name, scope, project, 1, Flow.CanonicalJson(flow), flow.Hash, null, "owner", at);
        object[] events = retired
            ? [drafted, new DefinitionPublished(id, "owner", at), new DefinitionRetired(id, "owner", at)]
            : [drafted, new DefinitionPublished(id, "owner", at)];
        await using IDocumentSession session = store.LightweightSession(tenant);
        session.Events.StartStream<Definition>(id, events);
        await session.SaveChangesAsync(None);
    }

    private static async Task<List<Definition>> InNodeAsync(NodeHome home)
    {
        await using IDocumentStore store = StoreConfiguration.Open(home.DatabasePath);
        await using IQuerySession session = store.QuerySession(Tenants.Node);
        return [.. await session.Query<Definition>().ToListAsync(None)];
    }
}
