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
/// follow with no closes flag, which reads as not closing.
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
