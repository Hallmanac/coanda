using Coanda.Core;
using Coanda.Modules.Software.Repositories;

namespace Coanda.Tests.Modules.Software.Repositories;

/// <summary>
/// The module's record of flow run ends: a note releases a wait on it and answers a later wait at once,
/// and it holds nothing for a flow run it was told to forget or whose wait was cancelled before a note.
/// </summary>
public sealed class FlowRunEndsTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task A_note_releases_a_wait_already_held_and_answers_a_later_one_at_once()
    {
        var ends = new FlowRunEnds();
        Guid flowRunId = Ids.New();
        Task waiting = ends.ReachedAsync(flowRunId, CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        ends.Note(flowRunId);

        await waiting.WaitAsync(Patience);
        Assert.True(ends.ReachedAsync(flowRunId, CancellationToken.None).IsCompletedSuccessfully);
    }

    [Fact]
    public void A_forgotten_flow_run_leaves_no_entry_and_a_later_wait_on_it_waits_for_a_new_note()
    {
        var ends = new FlowRunEnds();
        Guid flowRunId = Ids.New();
        ends.Note(flowRunId);

        ends.Forget(flowRunId);

        Assert.Equal(0, ends.Count);
        Assert.False(ends.ReachedAsync(flowRunId, CancellationToken.None).IsCompleted);
    }

    [Fact]
    public async Task A_wait_cancelled_before_any_note_leaves_no_entry()
    {
        var ends = new FlowRunEnds();
        using var cancel = new CancellationTokenSource();
        Task waiting = ends.ReachedAsync(Ids.New(), cancel.Token);
        Assert.Equal(1, ends.Count);

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Patience));
        Assert.Equal(0, ends.Count);
    }
}
