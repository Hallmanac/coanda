using Coanda.Core;
using Coanda.Core.Agents;
using Coanda.Core.FlowRuns;
using Coanda.Core.Software;
using Coanda.Daemon.Executors;

namespace Coanda.Tests.Daemon.Executors;

public sealed class ScriptedExecutorTests
{
    private static readonly SpawnCaps Caps = new(2.50m, TimeSpan.FromMinutes(20));
    private static readonly ScriptEntry First = new("""{"verdict":"done"}""", "/transcripts/build-1.jsonl", new Measurement(1200, 0.10m, 3, TimeSpan.FromSeconds(40)));
    private static readonly ScriptEntry Second = new("""{"verdict":"blocked"}""", "/transcripts/build-2.jsonl", new Measurement(900, 0.07m, 2, TimeSpan.FromSeconds(25)));

    private static readonly CredentialProfile Work = new("claude-code", "work-profile", "/home/me/.claude-work", null);

    private static ScriptedExecutor Executor() => new(new Dictionary<string, IReadOnlyList<ScriptEntry>>
    {
        [ShippedAgents.Build.Name] = [First, Second],
    });

    private static ValueTask<SessionHandle> Spawn(ScriptedExecutor executor, string context = "Step: build", CredentialProfile? profile = null) =>
        executor.SpawnAsync(ShippedAgents.Build, context, "/work/tree", Caps, AnswerSchemas.Build, profile, null, CancellationToken.None);

    [Fact]
    public async Task Two_spawns_of_one_agent_receive_its_first_and_second_entries()
    {
        CancellationToken token = CancellationToken.None;
        ScriptedExecutor executor = Executor();

        SessionHandle one = await Spawn(executor, "first context", Work);
        SessionHandle two = await Spawn(executor, "second context");

        CollectedAnswer firstAnswer = await executor.CollectAsync(one, token);
        CollectedAnswer secondAnswer = await executor.CollectAsync(two, token);

        firstAnswer.Should().Be(new CollectedAnswer(First.Answer, First.TranscriptPath, First.Usage));
        secondAnswer.Should().Be(new CollectedAnswer(Second.Answer, Second.TranscriptPath, Second.Usage));
        executor.Spawns.Should().Equal(
            new ScriptedSpawn(one, "build", "first context", "/work/tree", Caps, "build", Work),
            new ScriptedSpawn(two, "build", "second context", "/work/tree", Caps, "build", null));
    }

    [Fact]
    public async Task Collecting_again_before_a_resume_returns_the_same_answer_and_a_resume_advances()
    {
        CancellationToken token = CancellationToken.None;
        ScriptedExecutor executor = Executor();
        SessionHandle handle = await Spawn(executor);

        (await executor.CollectAsync(handle, token)).Answer.Should().Be(First.Answer);
        (await executor.CollectAsync(handle, token)).Answer.Should().Be(First.Answer);

        await executor.ResumeAsync(handle, "Try again.", token);

        (await executor.CollectAsync(handle, token)).Answer.Should().Be(Second.Answer);
        executor.Resumes.Should().Equal(new ScriptedMessage(handle, "Try again."));
    }

    [Fact]
    public async Task Collecting_past_the_end_of_an_agents_script_names_the_agent_and_the_ordinal()
    {
        CancellationToken token = CancellationToken.None;
        ScriptedExecutor executor = Executor();
        await executor.CollectAsync(await Spawn(executor), token);
        await executor.CollectAsync(await Spawn(executor), token);
        SessionHandle third = await Spawn(executor);

        Func<Task> collect = async () => await executor.CollectAsync(third, token);

        (await collect.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("'build'").And.Contain("entry 3");
    }

    [Fact]
    public async Task An_agent_with_no_script_fails_at_its_first_ordinal()
    {
        CancellationToken token = CancellationToken.None;
        ScriptedExecutor executor = Executor();
        SessionHandle handle = await executor.SpawnAsync(
            ShippedAgents.ReviewerAdversarial, "context", "/work/tree", Caps, AnswerSchemas.Review, null, null, token);

        Func<Task> collect = async () => await executor.CollectAsync(handle, token);

        (await collect.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("'reviewer-adversarial'").And.Contain("entry 1");
    }

    [Fact]
    public async Task A_send_and_a_stop_are_read_back_from_the_records()
    {
        CancellationToken token = CancellationToken.None;
        ScriptedExecutor executor = Executor();
        SessionHandle handle = await Spawn(executor);

        await executor.SendAsync(handle, "The base branch moved.", token);
        await executor.StopAsync(handle, token);

        executor.Sends.Should().Equal(new ScriptedMessage(handle, "The base branch moved."));
        executor.Stops.Should().Equal(handle);
        executor.Resumes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_spawned_session_appears_in_list_until_it_is_stopped()
    {
        CancellationToken token = CancellationToken.None;
        ScriptedExecutor executor = Executor();
        SessionHandle one = await Spawn(executor);
        SessionHandle two = await Spawn(executor);

        (await executor.ListAsync(token)).Should().BeEquivalentTo([one, two]);

        await executor.StopAsync(one, token);

        (await executor.ListAsync(token)).Should().Equal(two);
    }

    [Fact]
    public async Task Reattach_returns_an_issued_handle_and_reports_an_unknown_or_stopped_one_as_gone()
    {
        CancellationToken token = CancellationToken.None;
        ScriptedExecutor executor = Executor();
        SessionHandle issued = await Spawn(executor);
        SessionHandle stopped = await Spawn(executor);
        await executor.StopAsync(stopped, token);

        (await executor.ReattachAsync(issued with { }, access: null, token)).Should().Be(ReattachedSession.OnSpawn(issued));
        (await executor.ReattachAsync(new SessionHandle(Ids.New(), "build"), access: null, token)).Should().BeNull();
        (await executor.ReattachAsync(stopped, access: null, token)).Should().BeNull();
    }

    [Fact]
    public async Task A_session_answers_from_the_agent_it_was_spawned_for_whatever_name_the_handle_carries()
    {
        CancellationToken token = CancellationToken.None;
        ScriptedExecutor executor = Executor();
        SessionHandle handle = await Spawn(executor);

        CollectedAnswer answer = await executor.CollectAsync(handle with { Agent = "someone-else" }, token);

        answer.Answer.Should().Be(First.Answer);
    }
}
