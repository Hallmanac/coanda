using System.Text.RegularExpressions;
using Coanda.Cli;
using Coanda.Core;
using Coanda.Core.Definitions;
using Coanda.Core.Flows;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Tests.Daemon;

namespace Coanda.Tests.Cli;

/// <summary>
/// <c>coa definition</c> drafts a definition from a file, lists and shows what can govern a project, and
/// publishes and retires one by its full or short id; a refusal is the daemon's rule, printed with exit code 2.
/// </summary>
public sealed partial class DefinitionCommandTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_person_drafts_lists_shows_publishes_and_retires_a_flow_through_coa()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        Flow custom = SoftwareFlows.Build with { Budget = SoftwareFlows.Build.Budget + 1m };
        string file = Write(temp, "build.json", DefinitionApiTests.Relaid(Flow.CanonicalJson(custom)));

        Ran drafted = await CoaAsync(temp, "definition", "draft", "flow", file);

        Assert.True(drafted.ExitCode == 0, drafted.Output + drafted.Error);
        DefinitionView draft = Assert.Single(await ListAsync(client), d => d.Scope == "project");
        Assert.Equal(("flow", "build", 1, custom.Hash), (draft.Kind, draft.Name, draft.Version, draft.Hash));
        Assert.Contains(draft.Id.ToString(), drafted.Output, StringComparison.Ordinal);
        Assert.Contains(custom.Hash, drafted.Output, StringComparison.Ordinal);

        Ran listed = await CoaAsync(temp, "definition", "list");
        Assert.True(listed.ExitCode == 0, listed.Output + listed.Error);
        Assert.Contains(Ids.Short(draft.Id), listed.Output, StringComparison.Ordinal);
        Assert.Contains(SoftwareDefinitions.Manifest.Version, listed.Output, StringComparison.Ordinal);

        Ran shown = await CoaAsync(temp, "definition", "show", Ids.Short(draft.Id));
        Assert.True(shown.ExitCode == 0, shown.Output + shown.Error);
        Assert.Contains(custom.Hash, shown.Output, StringComparison.Ordinal);
        Assert.Contains("\"Budget\"", shown.Output, StringComparison.Ordinal);

        Ran published = await CoaAsync(temp, "definition", "publish", Ids.Short(draft.Id));
        Assert.True(published.ExitCode == 0, published.Output + published.Error);
        Assert.Equal(custom.Hash, await BuildHashAsync(client));

        Ran retired = await CoaAsync(temp, "definition", "retire", draft.Id.ToString());
        Assert.True(retired.ExitCode == 0, retired.Output + retired.Error);
        Assert.Equal(SoftwareFlows.Build.Hash, await BuildHashAsync(client));
        Assert.Equal("retired", (await ListAsync(client)).Single(d => d.Id == draft.Id).Status);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_draft_that_breaks_a_rule_prints_the_daemons_reason_with_exit_code_2_and_records_nothing()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        using var client = new DaemonClient(temp.Home);
        string flow = SoftwareDefinitions.Read("flows", "build");
        string file = Write(temp, "build.json", flow);
        string unknownStep = Write(temp, "unknown-step.json", DefinitionApiTests.WithSystemStep(flow, "no-such-step"));
        IReadOnlyList<DefinitionView> before = await ListAsync(client);

        Ran notAnAgent = await CoaAsync(temp, "definition", "draft", "agent", file);
        Ran unknown = await CoaAsync(temp, "definition", "draft", "flow", unknownStep);
        Ran module = await CoaAsync(temp, "definition", "draft", "flow", file, "--scope", "module");

        Assert.Equal(2, notAnAgent.ExitCode);
        Assert.Contains(Squashed(Assert.Throws<DomainException>(() => ModuleDefinition.Read(DefinitionKind.Agent, flow)).Message), Squashed(notAnAgent.Output));
        Assert.Equal(2, unknown.ExitCode);
        Assert.Contains("'no-such-step'", Squashed(unknown.Output), StringComparison.Ordinal);
        Assert.Equal(2, module.ExitCode);
        Assert.Contains(
            Squashed("A definition cannot be drafted into module scope; module-scope definitions enter the store only when a module's definitions are imported."),
            Squashed(module.Output),
            StringComparison.Ordinal);
        Assert.Equal(before, await ListAsync(client));

        await daemon.StopAsync(None);
    }

    private static Task<Ran> CoaAsync(TemporaryHome temp, params string[] arguments) =>
        CoaProcess.RunAsync(temp.Home, None, [.. arguments, "--project", Project]);

    private static string Write(TemporaryHome temp, string name, string content)
    {
        string directory = Path.Combine(temp.Home.Path, "drafts");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<IReadOnlyList<DefinitionView>> ListAsync(DaemonClient client) =>
        await client.GetAsync<List<DefinitionView>>($"/definitions?project={Project}", None);

    private static async Task<string> BuildHashAsync(DaemonClient client) =>
        Assert.Single(await client.GetAsync<List<FlowView>>($"/flows?project={Project}", None), f => f.Name == "build").Hash;

    /// <summary>The text with every escape sequence and all white space removed, since the CLI wraps and may colour what it prints.</summary>
    private static string Squashed(string text) => Whitespace().Replace(Escapes().Replace(text, string.Empty), string.Empty);

    [GeneratedRegex(@"\x1b\[[0-9;]*[A-Za-z]")]
    private static partial Regex Escapes();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
