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

    private const string Rule =
        "Vouch only for nodes you run yourself. Every node in the project accepts what a vouched node records, and this build neither checks the role of the node that signed what it imports nor asks you to accept a project's commands before this node runs them; either one would lift this rule.";

    private const string NotYetTrusted =
        "This node's key is not yet trusted in project alpha by the records this node holds: no vouch that counts for it has arrived at this node. A node counts the vouch recorded here only once that node trusts this node's key.";

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
                Rule,
            ],
            NodeVouchCommand.Describe("alpha", new NodeVouchResult(Peer, Key, true, true, true, null, true)));
        Assert.Equal(
            [
                $"Project alpha already holds this vouch for node {Peer:D} with the key {Key}; nothing new was recorded.",
                "The vouch is recorded but not yet pushed: the remote refused. Run the same command again to push it.",
                Rule,
            ],
            NodeVouchCommand.Describe("alpha", new NodeVouchResult(Peer, Key, false, false, false, "the remote refused.", true)));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Every_vouch_answer_ends_with_the_vouching_rule_and_an_untrusted_one_says_so_just_before_it(bool recorded, bool pushed)
    {
        var vouched = new NodeVouchResult(Peer, Key, recorded, false, pushed, pushed ? null : "the remote refused.", false);

        IReadOnlyList<string> untrusted = NodeVouchCommand.Describe("alpha", vouched);
        IReadOnlyList<string> trusted = NodeVouchCommand.Describe("alpha", vouched with { Trusted = true });

        Assert.Equal(Rule, untrusted[^1]);
        Assert.Equal(NotYetTrusted, untrusted[^2]);
        Assert.Equal(Rule, trusted[^1]);
        Assert.DoesNotContain(NotYetTrusted, trusted);
        Assert.Equal(untrusted.Count - 1, trusted.Count);
    }

    [Fact]
    public void The_vouching_rule_names_who_to_vouch_for_what_a_vouch_grants_and_both_conditions_that_would_lift_it()
    {
        Assert.Contains("Vouch only for nodes you run yourself.", Rule, StringComparison.Ordinal);
        Assert.Contains("Every node in the project accepts what a vouched node records", Rule, StringComparison.Ordinal);
        Assert.Contains("checks the role of the node that signed what it imports", Rule, StringComparison.Ordinal);
        Assert.Contains("asks you to accept a project's commands before this node runs them", Rule, StringComparison.Ordinal);
        Assert.Contains("this build neither", Rule, StringComparison.Ordinal);
    }

    [Fact]
    public void The_not_yet_trusted_line_speaks_only_of_what_this_node_holds()
    {
        Assert.Contains("not yet trusted in project alpha by the records this node holds", NotYetTrusted, StringComparison.Ordinal);
        Assert.Contains("only once that node trusts this node's key", NotYetTrusted, StringComparison.Ordinal);
        Assert.DoesNotContain("no node", NotYetTrusted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_join_prints_the_project_this_node_each_origins_import_and_whether_this_node_is_trusted()
    {
        var joined = new JoinResult(
            "alpha",
            Node,
            Key,
            false,
            true,
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
        Assert.Equal(
            "The join of project alpha is not finished: the import has not yet stored the owner's registration of it. Run the same coa join again to pick up from where it stopped.",
            JoinCommand.Describe(joined with { Registered = false })[0]);
    }
}
