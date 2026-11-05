using Coanda.Core;
using Coanda.Daemon;
using Coanda.Daemon.Api;
using Coanda.Daemon.Signing;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Tests.Daemon;

/// <summary>
/// The node identity: made once at the daemon's first start on a home and read at every later one,
/// never made twice even by two starts at once, and never made over a home that holds part of one.
/// Its owner-only access is held by <see cref="OwnerOnlyAccessTests"/>.
/// </summary>
public sealed class NodeIdentityTests
{
    [Fact]
    public async Task The_first_start_makes_the_identity_and_a_later_start_over_the_same_home_reads_the_same_one()
    {
        using var temporary = new TemporaryHome();
        NodeHome home = temporary.Home;
        Assert.False(File.Exists(home.NodeIdentityPath));

        NodeIdentity first = await StartedIdentityAsync(home);
        byte[] written = await File.ReadAllBytesAsync(home.NodeIdentityPath, CancellationToken.None);
        NodeIdentity second = await StartedIdentityAsync(home);

        Assert.Equal(7, first.NodeId.Version);
        Assert.Equal(first.NodeId, second.NodeId);
        Assert.Equal(first.Key.PublicKey, second.Key.PublicKey);
        Assert.Equal(first.Key.PrivateKeyText, second.Key.PrivateKeyText);
        Assert.Equal(written, await File.ReadAllBytesAsync(home.NodeIdentityPath, CancellationToken.None));
        Assert.Equal(Ed25519Key.PublicKeyLength, Convert.FromBase64String(first.Key.PublicKey).Length);
    }

    [Fact]
    public async Task Two_starts_making_the_identity_at_once_over_one_empty_home_both_get_the_same_one()
    {
        for (int round = 0; round < 10; round++)
        {
            using var temporary = new TemporaryHome();
            using var together = new Barrier(2);

            NodeIdentity[] made = await Task.WhenAll(
                Task.Run(() => MakeAfterAsync(together, temporary.Home)),
                Task.Run(() => MakeAfterAsync(together, temporary.Home)));

            Assert.Equal(made[0].NodeId, made[1].NodeId);
            Assert.Equal(made[0].Key.PrivateKeyText, made[1].Key.PrivateKeyText);
            Assert.Equal(made[0].NodeId, NodeIdentity.Read(temporary.Home)?.NodeId);
            AssertNoWrittenFileLeft(temporary.Home);
        }
    }

    [Fact]
    public async Task A_start_that_finds_an_identity_another_start_has_just_written_uses_that_one()
    {
        using var temporary = new TemporaryHome();
        NodeHome home = temporary.Home;
        NodeIdentity? other = null;
        NodeIdentity.BeforeLock.Value = () =>
        {
            // the other start runs whole after this one found no identity and before it takes the lock
            NodeIdentity.BeforeLock.Value = null;
            other = NodeIdentity.EnsureAsync(home, CancellationToken.None).AsTask().GetAwaiter().GetResult();
        };

        NodeIdentity mine;
        try
        {
            mine = await NodeIdentity.EnsureAsync(home, CancellationToken.None);
        }
        finally
        {
            NodeIdentity.BeforeLock.Value = null;
        }

        Assert.NotNull(other);
        Assert.Equal(other.NodeId, mine.NodeId);
        Assert.Equal(other.Key.PrivateKeyText, mine.Key.PrivateKeyText);
        Assert.Equal(mine.NodeId, NodeIdentity.Read(home)?.NodeId);
        AssertNoWrittenFileLeft(home);
    }

    [Fact]
    public async Task A_start_interrupted_before_its_identity_is_moved_into_place_leaves_no_identity_and_the_next_start_makes_one()
    {
        using var temporary = new TemporaryHome();
        NodeHome home = temporary.Home;
        NodeIdentity.BeforeMove.Value = () => throw new IOException("interrupted");
        try
        {
            await Assert.ThrowsAsync<IOException>(() => NodeIdentity.EnsureAsync(home, CancellationToken.None).AsTask());
        }
        finally
        {
            NodeIdentity.BeforeMove.Value = null;
        }

        Assert.False(File.Exists(home.NodeIdentityPath));
        AssertNoWrittenFileLeft(home);
        NodeIdentity made = await NodeIdentity.EnsureAsync(home, CancellationToken.None);
        Assert.Equal(made.NodeId, NodeIdentity.Read(home)?.NodeId);
    }

    public static TheoryData<string, string> PartialIdentities => new()
    {
        { """{"nodeId":"0196f3a0-2b3c-7d4e-9f50-61728394a5b6"}""", "a node id but no key" },
        { """{"privateKey":"nWGxne/9WmC6hEr0kuwsxERJxWl7MmkZcDusAxyuf2A="}""", "a key but no node id" },
        { """{"nodeId":"0196f3a0-2b3c-7d4e-9f50-6172""", "does not parse" },
        { "", "does not parse" },
        { """{"nodeId":"0196f3a0-2b3c-4d4e-9f50-61728394a5b6","privateKey":"nWGxne/9WmC6hEr0kuwsxERJxWl7MmkZcDusAxyuf2A="}""", "not a UUIDv7" },
        { """{"nodeId":"0196f3a0-2b3c-7d4e-9f50-61728394a5b6","privateKey":"nWGx"}""", "its key is not 32 bytes" },
    };

    [Theory]
    [MemberData(nameof(PartialIdentities))]
    public async Task A_home_holding_part_of_an_identity_is_refused_at_start_naming_the_file_and_left_as_it_is(string content, string fault)
    {
        using var temporary = new TemporaryHome();
        NodeHome home = temporary.Home;
        await File.WriteAllTextAsync(home.NodeIdentityPath, content, CancellationToken.None);
        byte[] before = await File.ReadAllBytesAsync(home.NodeIdentityPath, CancellationToken.None);

        DomainException refused = await Assert.ThrowsAsync<DomainException>(() => Daemons.StartAsync(home, CancellationToken.None));

        Assert.Contains(home.NodeIdentityPath, refused.Message, StringComparison.Ordinal);
        Assert.Contains(fault, refused.Message, StringComparison.Ordinal);
        Assert.Equal(before, await File.ReadAllBytesAsync(home.NodeIdentityPath, CancellationToken.None));
        AssertNoWrittenFileLeft(home);
        Assert.False(File.Exists(home.DaemonInfoPath));
    }

    private static async Task<NodeIdentity> MakeAfterAsync(Barrier together, NodeHome home)
    {
        together.SignalAndWait(TimeSpan.FromSeconds(30));
        return await NodeIdentity.EnsureAsync(home, CancellationToken.None);
    }

    private static async Task<NodeIdentity> StartedIdentityAsync(NodeHome home)
    {
        await using RunningDaemon daemon = await Daemons.StartAsync(home, CancellationToken.None);
        NodeIdentity identity = daemon.Services.GetRequiredService<NodeIdentity>();
        await daemon.StopAsync(CancellationToken.None);
        return identity;
    }

    /// <summary>No file a start wrote on its way to the identity file is left beside it.</summary>
    private static void AssertNoWrittenFileLeft(NodeHome home) =>
        Assert.DoesNotContain(Directory.EnumerateFiles(home.Path), path => path.EndsWith(".tmp", StringComparison.Ordinal));
}
