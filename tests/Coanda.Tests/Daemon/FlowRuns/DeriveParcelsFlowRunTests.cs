using System.Text.Json;
using Coanda.Cli;
using Coanda.Core.FlowRuns;
using Coanda.Core.Flows;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Core.Types;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Executors;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Parcels;
using Coanda.Daemon.SystemSteps;
using Fisher;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon.FlowRuns;

/// <summary>
/// The core's derive-parcels step in a flow run on the scripted executor: an agent step answers the parcels to
/// derive, the step creates them with a derived-from relation to the flow run's parcel, and performing the step
/// again, for the same visit or as a new visit on a resume, creates none of them twice.
/// </summary>
public sealed class DeriveParcelsFlowRunTests
{
    private const string Project = "alpha";
    private static readonly CancellationToken None = CancellationToken.None;

    /// <summary>The type the flow runs on, which may derive <see cref="Piece"/>.</summary>
    private static readonly ParcelType Source = SoftwareTypes.UnownedTask with { Name = "source", FlowBindings = [], Derives = ["piece"] };

    /// <summary>The type derived, bound to no flow, so nothing starts on a derived parcel.</summary>
    private static readonly ParcelType Piece = SoftwareTypes.UnownedTask with { Name = "piece", FlowBindings = [] };

    /// <summary>A type that may derive a type the project does not resolve as well as <see cref="Piece"/>.</summary>
    private static readonly ParcelType Haunted = Source with { Name = "haunted", Derives = ["piece", "ghost"] };

    /// <summary>A type that declares no type it may derive.</summary>
    private static readonly ParcelType Barren = Source with { Name = "barren", Derives = [] };

    private static readonly string PlanAnswer =
        """{"verdict":"done","summary":"two pieces","parcels":[{"type":"piece","title":"First piece","note":"ignored"},{"type":"Piece","title":"Second piece"}]}""";

    /// <summary>A flow whose agent step answers the parcels and whose derive step goes to <paramref name="afterDerive"/>.</summary>
    private static Flow Split(string afterDerive, string from = "plan") => new()
    {
        Name = "split",
        Accepts = [Source.Name, Haunted.Name, Barren.Name],
        Outcome = "split",
        Budget = 10m,
        SpawnCap = 1,
        Steps =
        [
            new FlowStep
            {
                Name = "plan",
                Agent = "build",
                AnswerSchema = "build",
                Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of("done"), VisitCap = 1, Target = "derive" }],
            },
            new FlowStep
            {
                Name = "derive",
                SystemStep = CoreSystemSteps.DeriveParcels,
                Arguments = new Dictionary<string, JsonElement> { [DeriveParcelsSystemStep.FromArgument] = FlowValue.Of(from) },
                Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of("done"), VisitCap = 2, Target = afterDerive }],
            },
        ],
    };

    [Fact]
    public async Task A_flow_run_derives_the_two_parcels_its_agent_answered_and_a_second_dispatch_of_the_same_visit_finds_them_unchanged()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await StartAsync(temp);
        using var client = new DaemonClient(temp.Home);
        (ParcelView source, Guid flowRunId) = await StartedAsync(daemon, client, Split(Flow.End));

        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "ended");

        IReadOnlyList<Guid> ids = ParcelIdsOf(ended.Steps[^1].Result);
        Assert.Equal(2, ids.Count);
        ParcelService parcels = daemon.Services.GetRequiredService<ParcelService>();
        IReadOnlyList<Parcel> derived = await ShowAllAsync(parcels, ids);
        Assert.Equal(["First piece", "Second piece"], derived.Select(p => p.Title));
        for (int i = 0; i < derived.Count; i++)
        {
            Parcel parcel = derived[i];
            Assert.Equal(Piece.Name, parcel.TypeName);
            Assert.Equal(Piece.EntryStages[0], parcel.Stage);
            Assert.Equal(DeriveParcelsSystemStep.PrincipalFor(flowRunId).Id, parcel.CreatedBy);
            Assert.Equal(DeriveParcelsSystemStep.OriginOf(flowRunId, "plan", 1, i + 1), parcel.Origin);
            ParcelRelation relation = Assert.Single(parcel.Relations);
            Assert.Equal(RelationKinds.DerivedFrom, relation.Kind);
            Assert.Equal(source.Id, relation.RelatedParcelId);
            Assert.Equal(flowRunId, relation.FlowRunId);
        }

        IReadOnlyList<long> versions = await StreamVersionsAsync(daemon, ids);

        // The same dispatch sent again, as a daemon restart sends it, creates nothing and lists the same parcels.
        FlowRun flowRun = await FlowRunsOf(daemon).ShowAsync(Principal.LocalOwner, Project, flowRunId, None) ?? throw new InvalidOperationException("No flow run.");
        FlowRunStepEntry visit = flowRun.Steps[^1];
        Flow flow = Split(Flow.End);
        var again = new SystemStepDispatch(
            FlowRunAdvancer.PrincipalFor(flowRunId), Project, flowRunId, source.Id, flow, flow.Step("derive") ?? throw new InvalidOperationException("No derive step."),
            Resumed: false, Visit: 1, visit.DispatchId ?? throw new InvalidOperationException("No dispatch id."), WorkPath: temp.Home.Path);
        SystemStepAnswer answer = await daemon.Services.GetRequiredService<FlowSystemSteps>().DispatchAsync(again, None);

        Assert.Equal(ids, ParcelIdsOf(Assert.IsType<SystemStepAnswer.Done>(answer).Result));
        Assert.Equal(versions, await StreamVersionsAsync(daemon, ids));
        Assert.Equal(3, (await parcels.ListAsync(Principal.LocalOwner, Project, None)).Count);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_resume_that_performs_a_recorded_derive_step_again_as_a_new_visit_creates_no_parcel_beyond_the_first_visits()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await StartAsync(temp);
        using var client = new DaemonClient(temp.Home);
        (_, Guid flowRunId) = await StartedAsync(daemon, client, Split(Flow.Pause));

        FlowRunView first = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "paused");
        Assert.Equal(["plan", "derive"], first.Steps.Select(s => s.Step));
        IReadOnlyList<Guid> ids = ParcelIdsOf(first.Steps[^1].Result);
        Assert.Equal(2, ids.Count);

        await FlowRunDriver.ResumeAsync(client, Project, flowRunId, "Derive again.");
        FlowRunView? second = null;
        await FlowRunDriver.UntilAsync(
            async () =>
            {
                second = await FlowRunDriver.ShowAsync(client, Project, flowRunId);
                return second is { Status: "paused", Steps.Count: 3 } && second.Steps[^1].RecordedAt is not null;
            },
            "the resumed derive step records a second visit and pauses");
        Assert.NotNull(second);

        Assert.Equal(["plan", "derive", "derive"], second.Steps.Select(s => s.Step));
        Assert.Equal(ids, ParcelIdsOf(second.Steps[^1].Result));
        ParcelService parcels = daemon.Services.GetRequiredService<ParcelService>();
        Assert.Equal(3, (await parcels.ListAsync(Principal.LocalOwner, Project, None)).Count);

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_new_answer_the_from_step_records_on_a_later_visit_derives_a_new_set()
    {
        static ScriptEntry Plan(string summary, string title, int ordinal) => new(
            $$"""{"verdict":"done","summary":"{{summary}}","parcels":[{"type":"piece","title":"{{title}}"}]}""", $"/transcripts/{ordinal}.jsonl", Scripts.Usage(ordinal));
        static FlowTransition On(string field, string value, string target) =>
            new() { Field = field, Value = FlowValue.Of(value), VisitCap = 3, Target = target };

        using var temp = new TemporaryHome();
        var executor = new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>
        {
            ["build"] = [Plan("more", "First round", 1), Plan("more", "Second round", 2), Plan("last", "Never derived", 3)],
        });
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        await Publishing.TypeAsync(daemon, Project, Piece);
        await Publishing.TypeAsync(daemon, Project, Source);
        using var client = new DaemonClient(temp.Home);
        Flow rounds = Split(Flow.End) with
        {
            Name = "rounds",
            Accepts = [Source.Name],
            SpawnCap = 3,
            Steps =
            [
                new FlowStep { Name = "plan", Agent = "build", AnswerSchema = "build", Transitions = [On("summary", "more", "derive"), On("summary", "last", Flow.End)] },
                Split(Flow.End).Steps[1] with { Transitions = [On("verdict", "done", "plan")] },
            ],
        };
        (_, Guid flowRunId) = await StartedAsync(daemon, client, rounds);

        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "ended");

        Assert.Equal(["plan", "derive", "plan", "derive", "plan"], ended.Steps.Select(s => s.Step));
        Guid first = Assert.Single(ParcelIdsOf(ended.Steps[1].Result));
        Guid second = Assert.Single(ParcelIdsOf(ended.Steps[3].Result));
        IReadOnlyList<Parcel> derived = await ShowAllAsync(daemon.Services.GetRequiredService<ParcelService>(), [first, second]);
        Assert.Equal(["First round", "Second round"], derived.Select(p => p.Title));
        Assert.Equal(
            [DeriveParcelsSystemStep.OriginOf(flowRunId, "plan", 1, 1), DeriveParcelsSystemStep.OriginOf(flowRunId, "plan", 2, 1)],
            derived.Select(p => p.Origin));

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_empty_list_derives_nothing_and_answers_done_with_no_parcel_ids()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await StartAsync(temp, """{"verdict":"done","parcels":[]}""");
        using var client = new DaemonClient(temp.Home);
        (_, Guid flowRunId) = await StartedAsync(daemon, client, Split(Flow.End));

        FlowRunView ended = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "ended");

        Assert.Empty(ParcelIdsOf(ended.Steps[^1].Result));
        Assert.Single(await daemon.Services.GetRequiredService<ParcelService>().ListAsync(Principal.LocalOwner, Project, None));

        await daemon.StopAsync(None);
    }

    /// <summary>An answer whose <c>parcels</c> array lists <paramref name="entries"/>, each a JSON value.</summary>
    private static string Listing(params string[] entries) => $$"""{"verdict":"done","parcels":[{{string.Join(",", entries)}}]}""";

    public static TheoryData<string, string> RefusedAnswers()
    {
        const string first = """{"type":"piece","title":"Fine"}""";
        return new TheoryData<string, string>
        {
            { """{"verdict":"done"}""", "the answer of step 'plan' has no 'parcels' array." },
            { """{"verdict":"done","parcels":{"type":"piece"}}""", "the answer of step 'plan' has no 'parcels' array." },
            {
                Listing([.. Enumerable.Repeat(first, DeriveParcelsSystemStep.MaxEntries + 1)]),
                $"the answer of step 'plan' lists {DeriveParcelsSystemStep.MaxEntries + 1} parcels, and at most {DeriveParcelsSystemStep.MaxEntries} are derived from one answer."
            },
            { Listing(first, "\"piece\""), "entry 2 of the answer of step 'plan' is not an object." },
            { Listing(first, """{"title":"No type"}"""), "entry 2 of the answer of step 'plan' has no string 'type'." },
            { Listing(first, """{"type":"piece","title":3}"""), "entry 2 of the answer of step 'plan' has no string 'title'." },
            {
                Listing(first, """{"type":"task","title":"Not derivable"}"""),
                "entry 2 of the answer of step 'plan' names the type 'task', which the flow run's parcel's type 'source' does not list as one it may derive; it may derive only piece."
            },
            {
                Listing(first, """{"type":"piece","title":"  "}"""),
                "entry 2 of the answer of step 'plan' has a title the parcel decider refuses: A parcel needs a title."
            },
            {
                Listing(first, $$"""{"type":"piece","title":"{{new string('x', ParcelCaps.TitleLength + 1)}}"}"""),
                $"entry 2 of the answer of step 'plan' has a title the parcel decider refuses: A parcel's title holds at most {ParcelCaps.TitleLength} characters"
            },
        };
    }

    [Theory]
    [MemberData(nameof(RefusedAnswers))]
    public async Task An_answer_the_step_refuses_pauses_naming_the_step_and_what_was_refused_and_creates_no_parcel(string answer, string refusal)
    {
        await AssertPausesAsync(answer, Source, Split(Flow.End), refusal);
    }

    [Fact]
    public async Task An_entry_naming_a_derivable_type_the_project_does_not_resolve_pauses_and_creates_no_parcel()
    {
        await AssertPausesAsync(
            """{"verdict":"done","parcels":[{"type":"piece","title":"Fine"},{"type":"ghost","title":"Boo"}]}""",
            Haunted,
            Split(Flow.End),
            "entry 2 of the answer of step 'plan' names the type 'ghost', which project 'alpha' resolves no parcel type of.");
    }

    [Fact]
    public async Task A_parcel_whose_type_declares_no_derive_list_derives_nothing()
    {
        await AssertPausesAsync(
            PlanAnswer,
            Barren,
            Split(Flow.End),
            "entry 1 of the answer of step 'plan' names the type 'piece', which the flow run's parcel's type 'barren' does not list as one it may derive; it declares no type it may derive.");
    }

    [Fact]
    public async Task A_parcel_created_before_its_type_could_derive_pauses_saying_it_keeps_the_earlier_version_with_a_lever_to_add_a_new_parcel()
    {
        ParcelType before = Source with { Name = "elder", Derives = [] };
        ParcelType after = before with { Derives = ["piece"] };
        Flow flow = Split(Flow.End) with { Accepts = [before.Name] };
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await StartAsync(temp);
        using var client = new DaemonClient(temp.Home);
        await Publishing.TypeAsync(daemon, Project, before);
        await Publishing.FlowAsync(daemon, Project, flow);
        ParcelView elder = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, before.Name, "Split me", null), None);
        await Publishing.TypeAsync(daemon, Project, after);
        FlowRunView started = await FlowRunDriver.StartAsync(client, Project, elder.Id, flow.Name);

        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, started.Id, "paused");

        Assert.Equal("derive", paused.CurrentStep);
        Assert.Equal(
            "Step 'derive' could not derive parcels: entry 1 of the answer of step 'plan' names the type 'piece', which the flow run's parcel's type 'elder' "
            + $"does not list as one it may derive; it declares no type it may derive. The parcel was created under version {before.Hash} of its type and keeps "
            + $"that version, so the version {after.Hash} the project holds now, which may derive 'piece', does not apply to it; resuming cannot derive from this parcel.",
            paused.PauseReason);
        Assert.Equal(FlowRunLevers.TypeVersionKept(before.Name), paused.Lever);
        Assert.Single(await daemon.Services.GetRequiredService<ParcelService>().ListAsync(Principal.LocalOwner, Project, None));

        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_from_step_with_no_recorded_answer_pauses_and_a_system_steps_result_is_not_read()
    {
        Flow flow = Split(Flow.End, from: "wait") with
        {
            Steps =
            [
                new FlowStep
                {
                    Name = "wait",
                    SystemStep = CoreSystemSteps.Timer,
                    Arguments = new Dictionary<string, JsonElement> { [TimerSystemStep.SecondsArgument] = FlowValue.Of(0) },
                    Transitions = [new FlowTransition { Field = "verdict", Value = FlowValue.Of("done"), VisitCap = 1, Target = "derive" }],
                },
                .. Split(Flow.End, from: "wait").Steps.Skip(1),
            ],
        };

        await AssertPausesAsync(PlanAnswer, Source, flow, "step 'wait' has recorded no answer on flow run", lever: FlowRunLevers.AnswerReadAgain("derive", "wait"));
    }

    private static async Task AssertPausesAsync(string answer, ParcelType type, Flow flow, string refusal, string? lever = null)
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await StartAsync(temp, answer);
        using var client = new DaemonClient(temp.Home);
        (_, Guid flowRunId) = await StartedAsync(daemon, client, flow, type);

        FlowRunView paused = await FlowRunDriver.UntilAsync(client, Project, flowRunId, "paused");

        Assert.Equal("derive", paused.CurrentStep);
        Assert.StartsWith($"Step 'derive' could not derive parcels: {refusal}", paused.PauseReason, StringComparison.Ordinal);
        string from = flow.Step("derive")?.Arguments[DeriveParcelsSystemStep.FromArgument].GetString() ?? string.Empty;
        Assert.Equal(lever ?? FlowRunLevers.AnswerReadAgain("derive", from), paused.Lever);
        Assert.Null(paused.Steps[^1].Result);
        // only the flow run's own parcel exists
        Assert.Single(await daemon.Services.GetRequiredService<ParcelService>().ListAsync(Principal.LocalOwner, Project, None));

        await daemon.StopAsync(None);
    }

    private static async Task<RunningDaemon> StartAsync(TemporaryHome temp, string? answer = null)
    {
        var executor = new ScriptedExecutor(new Dictionary<string, IReadOnlyList<ScriptEntry>>
        {
            ["build"] = [new ScriptEntry(answer ?? PlanAnswer, "/transcripts/1.jsonl", Scripts.Usage(1))],
        });
        RunningDaemon daemon = await Daemons.StartAsync(temp.Home, new DaemonOptions { Executor = executor }, None);
        await RegisteredProjects.AddAsync(daemon, Project, None);
        await Publishing.TypeAsync(daemon, Project, Piece);
        await Publishing.TypeAsync(daemon, Project, Source);
        await Publishing.TypeAsync(daemon, Project, Haunted);
        await Publishing.TypeAsync(daemon, Project, Barren);
        return daemon;
    }

    private static async Task<(ParcelView Source, Guid FlowRunId)> StartedAsync(RunningDaemon daemon, DaemonClient client, Flow flow, ParcelType? type = null)
    {
        await Publishing.FlowAsync(daemon, Project, flow);
        ParcelView source = await client.PostAsync<ParcelView>("/parcels", new ParcelAddRequest(Project, (type ?? Source).Name, "Split me", null), None);
        FlowRunView started = await FlowRunDriver.StartAsync(client, Project, source.Id, flow.Name);
        return (source, started.Id);
    }

    private static FlowRunService FlowRunsOf(RunningDaemon daemon) => new(daemon.Services.GetRequiredService<IDocumentStore>(), TimeProvider.System);

    private static IReadOnlyList<Guid> ParcelIdsOf(JsonElement? result)
    {
        JsonElement document = result ?? throw new InvalidOperationException("The step recorded no result.");
        Assert.Equal(SystemStepAnswer.DoneVerdict, document.GetProperty(SystemStepAnswer.Verdict).GetString());
        return [.. document.GetProperty(DeriveParcelsSystemStep.ParcelIdsMember).EnumerateArray().Select(id => Guid.Parse(id.GetString() ?? string.Empty))];
    }

    private static async Task<IReadOnlyList<Parcel>> ShowAllAsync(ParcelService parcels, IReadOnlyList<Guid> ids)
    {
        List<Parcel> shown = [];
        foreach (Guid id in ids)
        {
            shown.Add(await parcels.ShowAsync(Principal.LocalOwner, Project, id, None) ?? throw new InvalidOperationException($"No parcel {id}."));
        }

        return shown;
    }

    private static async Task<IReadOnlyList<long>> StreamVersionsAsync(RunningDaemon daemon, IReadOnlyList<Guid> ids)
    {
        await using IQuerySession session = daemon.Services.GetRequiredService<IDocumentStore>().QuerySession(Project);
        List<long> versions = [];
        foreach (Guid id in ids)
        {
            versions.Add((await session.Events.FetchStreamStateAsync(id, None))?.Version ?? 0);
        }

        return versions;
    }
}
