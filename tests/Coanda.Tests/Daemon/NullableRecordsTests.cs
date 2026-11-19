using System.Text.Json;
using System.Text.Json.Nodes;
using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.FlowRuns;
using Coanda.Core.FlowRuns.Events;
using Coanda.Core.Parcels;
using Coanda.Core.Registry;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.FlowRuns;
using Coanda.Daemon.Store;
using Coanda.Daemon.Triggers;
using Fisher;
using JasperFx.Events;
using JasperFx.Events.Documents;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon;

/// <summary>
/// Two records earlier builds wrote are current shapes, not earlier ones, so a home holding them starts
/// and reads normally: an <c>agent_step_spawned</c> with no agent hash, model, or launch id, and a deferred
/// follow with no closes flag, which reads as not closing. A <c>flow_run_resumed</c> recorded before
/// resumes carried answers is a current shape too, and reads with no answer.
/// </summary>
public sealed class NullableRecordsTests
{
    private const string Alpha = "alpha";

    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task A_home_holding_an_agent_step_spawned_with_no_agent_hash_model_or_launch_id_starts_and_reads_it()
    {
        using var temp = new TemporaryHome();
        NodeHome home = temp.Home;
        RecordHomes.Restore(home, RecordHomes.RebuiltByEe75e50);
        Guid flowRunId = Guid.Parse("01a1ad6c-bc0b-727b-a228-9e5042800021");
        var at = new DateTimeOffset(2026, 11, 3, 6, 0, 0, TimeSpan.Zero);
        await using (IDocumentStore store = StoreConfiguration.Open(home.DatabasePath))
        {
            Guid parcelId;
            await using (IQuerySession query = store.QuerySession(Alpha))
            {
                parcelId = Assert.Single(await query.Query<Parcel>().ToListAsync(None)).Id;
            }

            await using IDocumentSession session = store.LightweightSession(Alpha);
            session.Events.StartStream<FlowRun>(
                flowRunId,
                new FlowRunStarted(flowRunId, parcelId, "build", 1, "sha256:flow", Principal.LocalOwner.Id, "implement", StepKind.AgentStep, "implementer", at),
                new AgentStepSpawned(flowRunId, "implement", "implementer", null, null, at, null),
                new FlowRunStopped(flowRunId, Principal.LocalOwner.Id, "Stopped before its agent answered", at));
            await session.SaveChangesAsync(None);
        }

        // As the builds before the agent hash, model, and launch id wrote it: without those members at all.
        SqliteConnection.ClearAllPools();
        using (SqliteConnection connection = RecordHomes.Open(home.DatabasePath))
        using (SqliteCommand earlier = connection.CreateCommand())
        {
            earlier.CommandText = "UPDATE fi_events SET data = json_remove(data, '$.agentHash', '$.model', '$.launchId') WHERE type = 'agent_step_spawned'";
            Assert.Equal(1, earlier.ExecuteNonQuery());
        }

        await using RunningDaemon daemon = await Daemons.StartAsync(home, None);
        FlowRun read = await daemon.Services.GetRequiredService<FlowRunService>().ShowAsync(Principal.LocalOwner, Alpha, flowRunId, None)
            ?? throw new InvalidOperationException("The flow run is gone.");
        FlowRunStepEntry step = Assert.Single(read.Steps);
        Assert.Equal(("implement", "implementer", null, null), (step.Step, step.Performer, step.AgentHash, step.Model));

        await using IQuerySession events = daemon.Services.GetRequiredService<IDocumentStore>().QuerySession(Alpha);
        IReadOnlyList<IEvent> stream = await events.Events.FetchStreamAsync(flowRunId, token: None);
        AgentStepSpawned spawned = Assert.Single(stream.Select(e => e.Data).OfType<AgentStepSpawned>());
        Assert.Equal((null, null, null), (spawned.AgentHash, spawned.Model, spawned.LaunchId));
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task A_home_holding_a_flow_run_resumed_with_no_answer_starts_and_reads_it_with_none()
    {
        using var temp = new TemporaryHome();
        NodeHome home = temp.Home;
        RecordHomes.Restore(home, RecordHomes.RebuiltByEe75e50);
        Guid flowRunId = Guid.Parse("01a1ad6c-bc0b-727b-a228-9e5042800022");
        var at = new DateTimeOffset(2026, 11, 3, 6, 0, 0, TimeSpan.Zero);
        await using (IDocumentStore store = StoreConfiguration.Open(home.DatabasePath))
        {
            Guid parcelId;
            await using (IQuerySession query = store.QuerySession(Alpha))
            {
                parcelId = Assert.Single(await query.Query<Parcel>().ToListAsync(None)).Id;
            }

            await using IDocumentSession session = store.LightweightSession(Alpha);
            session.Events.StartStream<FlowRun>(
                flowRunId,
                new FlowRunStarted(flowRunId, parcelId, "build", 1, "sha256:flow", Principal.LocalOwner.Id, "approve", StepKind.SystemStep, "wait-for-human", at),
                new FlowRunPaused(flowRunId, "Waits for a person.", "Resume it.", "flow-run", at),
                new FlowRunResumed(flowRunId, Principal.LocalOwner.Id, "Carry on.", at, "approve"),
                new FlowRunStopped(flowRunId, Principal.LocalOwner.Id, "Stopped after its resume", at));
            await session.SaveChangesAsync(None);
        }

        // As the builds before resumes carried answers wrote it: without the member at all.
        SqliteConnection.ClearAllPools();
        using (SqliteConnection connection = RecordHomes.Open(home.DatabasePath))
        using (SqliteCommand earlier = connection.CreateCommand())
        {
            earlier.CommandText = "UPDATE fi_events SET data = json_remove(data, '$.answer') WHERE type = 'flow_run_resumed'";
            Assert.Equal(1, earlier.ExecuteNonQuery());
        }

        await using RunningDaemon daemon = await Daemons.StartAsync(home, None);
        await using IQuerySession events = daemon.Services.GetRequiredService<IDocumentStore>().QuerySession(Alpha);
        IReadOnlyList<IEvent> stream = await events.Events.FetchStreamAsync(flowRunId, token: None);
        FlowRunResumed resumed = Assert.Single(stream.Select(e => e.Data).OfType<FlowRunResumed>());
        Assert.Equal(("Carry on.", null), (resumed.Note, resumed.Answer));
        FlowRun read = await events.Events.AggregateStreamAsync<FlowRun>(flowRunId, token: None)
            ?? throw new InvalidOperationException("The flow run is gone.");
        Assert.Null(Assert.Single(read.Steps).ResumeAnswer);
        await daemon.StopAsync(None);
    }

    [Fact]
    public async Task An_answer_and_a_pause_stored_without_the_hashes_of_their_sessions_files_read_back_with_none_and_replay_as_before()
    {
        using var temp = new TemporaryHome();
        NodeHome home = temp.Home;
        RecordHomes.Restore(home, RecordHomes.RebuiltByEe75e50);
        var at = new DateTimeOffset(2026, 11, 3, 6, 0, 0, TimeSpan.Zero);
        string promptHash = "sha256:" + new string('a', 64);
        string transcriptHash = "sha256:" + new string('b', 64);
        Guid answeredId = Ids.New();
        Guid pausedId = Ids.New();
        object[] answeredEvents = Launched(answeredId, at,
            new AgentStepAnswered(
                answeredId, "implement", "implementer", "sha256:agent", new ModelSpec("claude-code", "claude-sonnet-5-5", "high"),
                JsonSerializer.SerializeToElement(new { verdict = "done" }), new Measurement(10, 0.1m, 1, TimeSpan.FromSeconds(1)), at, promptHash, transcriptHash));
        object[] pausedEvents = Launched(pausedId, at,
            new FlowRunPaused(
                pausedId, "no valid answer after one retry", "/transcripts/1.jsonl.gz", "flow-run", at,
                new Measurement(20, 0.2m, 2, TimeSpan.FromSeconds(2)), PromptHash: promptHash, TranscriptHash: transcriptHash));
        await using (IDocumentStore store = StoreConfiguration.Open(home.DatabasePath))
        {
            await using IDocumentSession session = store.LightweightSession(Alpha);
            session.Events.StartStream<FlowRun>(answeredId, answeredEvents);
            session.Events.StartStream<FlowRun>(pausedId, pausedEvents);
            await session.SaveChangesAsync(None);
        }

        // As the builds before the hashes wrote them: without those members at all.
        SqliteConnection.ClearAllPools();
        using (SqliteConnection connection = RecordHomes.Open(home.DatabasePath))
        using (SqliteCommand earlier = connection.CreateCommand())
        {
            earlier.CommandText = "UPDATE fi_events SET data = json_remove(data, '$.promptHash', '$.transcriptHash') "
                + "WHERE type IN ('agent_step_answered', 'flow_run_paused') AND stream_id IN ($answered, $paused)";
            earlier.Parameters.AddWithValue("$answered", answeredId.ToString());
            earlier.Parameters.AddWithValue("$paused", pausedId.ToString());
            Assert.Equal(2, earlier.ExecuteNonQuery());
        }

        string[] streams = [answeredId.ToString(), pausedId.ToString()];
        Assert.All(
            RecordHomes.Events(home.DatabasePath).Where(e => streams.Contains(e.Stream) && e.Type is "agent_step_answered" or "flow_run_paused"),
            e => Assert.DoesNotContain("Hash\"", e.Data.Replace("agentHash\"", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal));

        await using (IDocumentStore reopened = StoreConfiguration.Open(home.DatabasePath))
        await using (IQuerySession query = reopened.QuerySession(Alpha))
        {
            object[] answeredRead = [.. (await query.Events.FetchStreamAsync(answeredId, token: None)).Select(e => e.Data)];
            object[] pausedRead = [.. (await query.Events.FetchStreamAsync(pausedId, token: None)).Select(e => e.Data)];
            AgentStepAnswered answered = Assert.Single(answeredRead.OfType<AgentStepAnswered>());
            FlowRunPaused paused = Assert.Single(pausedRead.OfType<FlowRunPaused>());
            Assert.Equal((null, null), (answered.PromptHash, answered.TranscriptHash));
            Assert.Equal((null, null), (paused.PromptHash, paused.TranscriptHash));
            Assert.Equal(((AgentStepAnswered)answeredEvents[^1]) with { PromptHash = null, TranscriptHash = null }, answered with { Answer = ((AgentStepAnswered)answeredEvents[^1]).Answer });
            Assert.Equal("""{"verdict":"done"}""", answered.Answer.GetRawText());
            Assert.Equal(((FlowRunPaused)pausedEvents[^1]) with { PromptHash = null, TranscriptHash = null }, paused);

            FlowRun answeredBefore = Replay(answeredEvents);
            FlowRun answeredAfter = Replay(answeredRead);
            FlowRun pausedBefore = Replay(pausedEvents);
            FlowRun pausedAfter = Replay(pausedRead);
            Assert.Equal(WithoutHashes(answeredBefore), WithoutHashes(answeredAfter));
            Assert.Equal(WithoutHashes(pausedBefore), WithoutHashes(pausedAfter));
            Assert.Equal((promptHash, transcriptHash), (answeredBefore.Steps[0].PromptHash, answeredBefore.Steps[0].TranscriptHash));
            Assert.Equal((null, null), (answeredAfter.Steps[0].PromptHash, answeredAfter.Steps[0].TranscriptHash));
            Assert.Equal((promptHash, transcriptHash), (pausedBefore.PausePromptHash, pausedBefore.PauseTranscriptHash));
            Assert.Equal((null, null), (pausedAfter.PausePromptHash, pausedAfter.PauseTranscriptHash));
        }
    }

    /// <summary>A flow run started at an agent step whose launch opened a session, then <paramref name="last"/>.</summary>
    private static object[] Launched(Guid flowRunId, DateTimeOffset at, object last) =>
    [
        new FlowRunStarted(flowRunId, Ids.New(), "build", 1, "sha256:flow", Principal.LocalOwner.Id, "implement", StepKind.AgentStep, "implementer", at),
        new AgentStepSpawned(flowRunId, "implement", "implementer", "sha256:agent", new ModelSpec("claude-code", "claude-sonnet-5-5", "high"), at, Ids.New()),
        new AgentStepSessionOpened(flowRunId, "implement", Ids.New(), at),
        last,
    ];

    private static FlowRun Replay(IReadOnlyList<object> events)
    {
        FlowRun flowRun = FlowRun.Create(Assert.IsType<FlowRunStarted>(events[0]));
        foreach (object recorded in events.Skip(1))
        {
            switch (recorded)
            {
                case AgentStepSpawned spawned:
                    flowRun.Apply(spawned);
                    break;
                case AgentStepSessionOpened opened:
                    flowRun.Apply(opened);
                    break;
                case AgentStepAnswered answered:
                    flowRun.Apply(answered);
                    break;
                case FlowRunPaused paused:
                    flowRun.Apply(paused);
                    break;
                default:
                    throw new InvalidOperationException($"The replay does not apply {recorded.GetType().Name}.");
            }
        }

        return flowRun;
    }

    /// <summary>The flow run as JSON with the members the hashes added taken out: every member that was there before them.</summary>
    private static string WithoutHashes(FlowRun flowRun)
    {
        JsonObject document = JsonSerializer.SerializeToNode(flowRun)?.AsObject() ?? throw new InvalidOperationException("The flow run did not serialize.");
        Assert.True(document.Remove(nameof(FlowRun.PausePromptHash)));
        Assert.True(document.Remove(nameof(FlowRun.PauseTranscriptHash)));
        foreach (JsonNode? step in document[nameof(FlowRun.Steps)]?.AsArray() ?? [])
        {
            JsonObject entry = step?.AsObject() ?? throw new InvalidOperationException("A step did not serialize.");
            Assert.True(entry.Remove(nameof(FlowRunStepEntry.PromptHash)));
            Assert.True(entry.Remove(nameof(FlowRunStepEntry.TranscriptHash)));
        }

        return document.ToJsonString();
    }

    [Fact]
    public async Task A_home_holding_a_deferred_follow_with_no_closes_flag_starts_and_reads_it_as_not_closing()
    {
        using var temp = new TemporaryHome();
        NodeHome home = temp.Home;
        RecordHomes.Restore(home, RecordHomes.RebuiltByEe75e50);
        using (SqliteConnection connection = RecordHomes.Open(home.DatabasePath))
        using (SqliteCommand earlier = connection.CreateCommand())
        {
            earlier.CommandText = "UPDATE fi_doc_deferredfollows SET data = json_remove(data, '$.pending[0].closes')";
            Assert.Equal(1, earlier.ExecuteNonQuery());
        }

        Assert.DoesNotContain("closes", Assert.Single(RecordHomes.Documents(home.DatabasePath, "fi_doc_deferredfollows")).Data, StringComparison.Ordinal);

        await using RunningDaemon daemon = await Daemons.StartAsync(home, None);
        await using IQuerySession session = daemon.Services.GetRequiredService<IDocumentStore>().QuerySession(Alpha);
        DeferredFollows follows = Assert.Single(await session.Query<DeferredFollows>().ToListAsync(None));
        PendingFollow follow = Assert.Single(follows.Pending);
        Assert.Equal(("merge", false), (follow.Flow, follow.Closes));
        await daemon.StopAsync(None);
    }
}
