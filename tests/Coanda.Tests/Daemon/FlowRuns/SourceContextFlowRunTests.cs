using System.Globalization;
using System.Text.Json;
using Coanda.Cli;
using Coanda.Core.Flows;
using Coanda.Core.Types;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Executors;
using Coanda.Daemon.SystemSteps;

namespace Coanda.Tests.Daemon.FlowRuns;

/// <summary>
/// An agent step's spawn, in a flow run on the scripted executor, carries what its sources of a named
/// flow and of the origin read: another flow run's answer on the same parcel, and the parcel a derived
/// parcel came from with the answers of the flow run that derived it.
/// </summary>
public sealed class SourceContextFlowRunTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    private static readonly ParcelType Source = SoftwareTypes.UnownedTask with { Name = "source", FlowBindings = [], Derives = ["piece"] };
    private static readonly ParcelType Piece = SoftwareTypes.UnownedTask with { Name = "piece", FlowBindings = [] };

    private static FlowTransition Done(string target, int visitCap = 1) =>
        new() { Field = "verdict", Value = FlowValue.Of("done"), VisitCap = visitCap, Target = target };

    private static Flow OneStep(string name, string accepts, FlowStep step) => new()
    {
        Name = name,
        Accepts = [accepts],
        Outcome = "done",
        Budget = 10m,
        SpawnCap = 1,
        Steps = [step with { Agent = "build", AnswerSchema = "build", Transitions = [Done(Flow.End)] }],
    };

    private static readonly Flow Split = new()
    {
        Name = "split",
        Accepts = [Source.Name],
        Outcome = "split",
        Budget = 10m,
        SpawnCap = 1,
        Steps =
        [
            new FlowStep { Name = "plan", Agent = "build", AnswerSchema = "build", Transitions = [Done("derive")] },
            new FlowStep
            {
                Name = "derive",
                SystemStep = CoreSystemSteps.DeriveParcels,
                Arguments = new Dictionary<string, JsonElement> { [DeriveParcelsSystemStep.FromArgument] = FlowValue.Of("plan") },
                Transitions = [Done(Flow.End)],
            },
        ],
    };

    private static ScriptEntry Answer(string json, int ordinal) => new(json, $"/transcripts/{ordinal}.jsonl", Scripts.Usage(ordinal));

    private static string Recorded(FlowRunStepView visit) =>
        $"recorded {visit.RecordedAt?.ToString("O", CultureInfo.InvariantCulture)}: {Fences.Inline((visit.Answer ?? visit.Result)?.GetRawText() ?? "missing")}";

    [Fact]
    public async Task A_spawn_carries_another_flow_runs_answer_on_its_parcel_and_its_parcels_origin()
    {
        using var temp = new TemporaryHome();
        var executor = new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>
        {
            ["build"] =
            [
                Answer("""{"verdict":"done","summary":"one piece","parcels":[{"type":"piece","title":"The piece"}]}""", 1),
                Answer("""{"verdict":"done","summary":"asked and answered"}""", 2),
                Answer("""{"verdict":"done","summary":"read"}""", 3),
            ],
        });
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        await Publishing.TypeAsync(daemon, Project, Piece);
        await Publishing.TypeAsync(daemon, Project, Source);
        Flow ask = OneStep("ask", Piece.Name, new FlowStep { Name = "ask" });
        Flow read = OneStep("read", Piece.Name, new FlowStep
        {
            Name = "read",
            Sources = [new FlowSource { Flow = "ASK", Step = "ask" }, new FlowSource { Origin = true }, new FlowSource { Type = "absent" }],
        });
        await Publishing.FlowAsync(daemon, Project, Split);
        await Publishing.FlowAsync(daemon, Project, ask);
        await Publishing.FlowAsync(daemon, Project, read);
        using var client = new DaemonClient(temp.Home);

        ParcelView source = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, Source.Name, "Split me", null), None);
        FlowRunView split = await FlowRunDriver.UntilAsync(client, Project, (await FlowRunDriver.StartAsync(client, Project, source.Id, Split.Name)).Id, "ended");
        JsonElement derived = split.Steps[^1].Result ?? throw new InvalidOperationException("The derive step recorded no result.");
        Guid piece = Guid.Parse(derived.GetProperty(DeriveParcelsSystemStep.ParcelIdsMember)[0].GetString() ?? string.Empty);
        FlowRunView asked = await FlowRunDriver.UntilAsync(client, Project, (await FlowRunDriver.StartAsync(client, Project, piece, ask.Name)).Id, "ended");
        await FlowRunDriver.UntilAsync(client, Project, (await FlowRunDriver.StartAsync(client, Project, piece, read.Name)).Id, "ended");

        string[] context = executor.Spawns[2].Context.Split('\n');
        Assert.Equal(
            [
                $"Source flow ASK step ask: flow run {asked.Id}, visit 1, {Recorded(asked.Steps[0])}",
                $"Source origin parcel: parcel {source.Id}, type source, stage {source.Stage}, title `Split me`",
                $"Source origin flow run: flow run {split.Id} of flow split",
                $"- step plan: visit 1, {Recorded(split.Steps[0])}",
                $"- step derive: visit 1, {Recorded(split.Steps[1])}",
                "Source open parcels of type absent: type absent is not present in this project",
            ],
            context[^6..]);
        Assert.Contains("asked and answered", context[^6], StringComparison.Ordinal);

        await daemon.StopAsync(None);
    }
}
