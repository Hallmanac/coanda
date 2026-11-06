using Coanda.Cli.Commands;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Identity;
using Coanda.Daemon.Transport;

namespace Coanda.Tests.Cli;

public sealed class NodeRenderingTests
{
    private static readonly Guid Node = new("0192a3b4-0000-7000-8000-000000000001");
    private static readonly Guid Peer = new("0192a3b4-0000-7000-8000-000000000002");
    private const string Key = "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=";

    [Fact]
    public void Node_show_prints_the_id_the_key_and_the_fingerprint_one_to_a_line()
    {
        Assert.Equal(
            [$"Node id:     {Node:D}", $"Public key:  {Key}", "Fingerprint: sha256:ab"],
            NodeShowCommand.Describe(new NodeView(Node, Key, "sha256:ab")));
    }

    [Fact]
    public void A_vouch_says_whether_it_is_pushed_or_recorded_and_not_yet_pushed_with_the_reason()
    {
        Assert.Equal(
            [
                "Recorded this node as the owner root of project alpha, and trusts its own key as the project's root.",
                $"Recorded a vouch in project alpha for node {Peer:D} with the key {Key}.",
                "The vouch is pushed.",
            ],
            NodeVouchCommand.Describe("alpha", new NodeVouchResult(Peer, Key, true, true, true, null)));
        Assert.Equal(
            [
                $"Project alpha already holds this vouch for node {Peer:D} with the key {Key}; nothing new was recorded.",
                "The vouch is recorded but not yet pushed: the remote refused. Run the same command again to push it.",
            ],
            NodeVouchCommand.Describe("alpha", new NodeVouchResult(Peer, Key, false, false, false, "the remote refused.")));
    }

    [Fact]
    public void A_join_prints_the_project_this_node_each_origins_import_and_whether_this_node_is_trusted()
    {
        var joined = new JoinResult(
            "alpha",
            Node,
            Key,
            false,
            [new OriginImport(Peer, 4, 4, null), new OriginImport(Node, 0, null, new ImportFault(1, ImportFaultKind.UntrustedSigner, "Line 1 is untrusted."))],
            ["Repo 'repo' could not be fetched."],
            false);

        Assert.Equal(
            [
                "Joined project alpha.",
                $"Node id:     {Node:D}",
                $"Public key:  {Key}",
                $"Origin {Peer:D}: stored 4 lines, holds through line 4",
                $"Origin {Node:D}: stored 0 lines, holds none of its lines; stopped: Line 1 is untrusted.",
                "Not read: Repo 'repo' could not be fetched.",
                "This node's key is not yet trusted in the project: the owner's vouch for it has not arrived. The owner's node stores what this node records once that vouch is recorded.",
            ],
            JoinCommand.Describe(joined));
        Assert.Equal("This node already holds project alpha under the same id; the join changed nothing.", JoinCommand.Describe(joined with { AlreadyHeld = true, Trusted = true })[0]);
    }
}
