using System.ComponentModel;
using Coanda.Daemon;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Identity;
using Coanda.Daemon.Transport;
using Spectre.Console.Cli;

namespace Coanda.Cli.Commands;

/// <summary>
/// Prints this node's id, its public key, and the key's fingerprint, one to a line. These commands write plain
/// lines, never wrapped to the terminal's width, so a key or a fingerprint is copied whole.
/// </summary>
public sealed class NodeShowCommand : AsyncCommand
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        NodeView node = await client.GetAsync<NodeView>("/node", cancellationToken);
        foreach (string line in Describe(node))
        {
            Console.Out.WriteLine(line);
        }

        return 0;
    }

    public static IReadOnlyList<string> Describe(NodeView node) =>
    [
        $"Node id:     {node.NodeId:D}",
        $"Public key:  {node.PublicKey}",
        $"Fingerprint: {node.Fingerprint}",
    ];
}

public sealed class NodeVouchSettings : ProjectSettings
{
    [CommandArgument(0, "<NODE-ID>")]
    [Description("The vouched node's id, as its coa node show prints it.")]
    public required string NodeId { get; init; }

    [CommandArgument(1, "<PUBLIC-KEY>")]
    [Description("The vouched node's public key, copied exactly from its coa node show.")]
    public required string Key { get; init; }
}

/// <summary>
/// Vouches, in a project, for a node id and its exact key, then publishes the project: 0 when the vouch is
/// pushed, 1 when it is recorded and not yet pushed.
/// </summary>
public sealed class NodeVouchCommand : AsyncCommand<NodeVouchSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, NodeVouchSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        string project = settings.ResolveProject();
        NodeVouchResult vouched = await client.PostAsync<NodeVouchResult>("/node/vouch", new NodeVouchRequest(project, settings.NodeId, settings.Key), cancellationToken);
        foreach (string line in Describe(project, vouched))
        {
            Console.Out.WriteLine(line);
        }

        return vouched.Pushed
            ? 0
            : 1;
    }

    public static IReadOnlyList<string> Describe(string project, NodeVouchResult vouched)
    {
        List<string> lines = [];
        if (vouched.RootRecorded)
        {
            lines.Add($"Recorded this node as the owner root of project {project}, and trusts its own key as the project's root.");
        }

        lines.Add(vouched.Recorded
            ? $"Recorded a vouch in project {project} for node {vouched.NodeId:D} with the key {vouched.Key}."
            : $"Project {project} already holds this vouch for node {vouched.NodeId:D} with the key {vouched.Key}; nothing new was recorded.");
        lines.Add(vouched.Pushed
            ? "The vouch is pushed."
            : $"The vouch is recorded but not yet pushed: {vouched.Reason} Run the same command again to push it.");
        return lines;
    }
}

public sealed class JoinSettings : CommandSettings
{
    [CommandArgument(0, "<REMOTE>")]
    [Description("The project's remote, exactly as the owner's coa project show prints it.")]
    public required string Remote { get; init; }

    [CommandOption("--root <FINGERPRINT>")]
    [Description("The fingerprint of the owner's root key, as the owner's coa node show prints it, carried by a person and not read from the remote.")]
    public required string Root { get; init; }
}

/// <summary>Joins the project at a remote whose owner's root key has the given fingerprint, and imports it at once.</summary>
public sealed class JoinCommand : AsyncCommand<JoinSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, JoinSettings settings, CancellationToken cancellationToken)
    {
        using var client = new DaemonClient(NodeHome.Resolve());
        JoinResult joined = await client.PostAsync<JoinResult>("/join", new JoinRequest(settings.Remote, settings.Root), cancellationToken);
        foreach (string line in Describe(joined))
        {
            Console.Out.WriteLine(line);
        }

        return 0;
    }

    public static IReadOnlyList<string> Describe(JoinResult joined)
    {
        List<string> lines =
        [
            joined.AlreadyHeld
                ? $"This node already holds project {joined.Project} under the same id; the join changed nothing."
                : $"Joined project {joined.Project}.",
            $"Node id:     {joined.NodeId:D}",
            $"Public key:  {joined.PublicKey}",
        ];
        foreach (OriginImport origin in joined.Origins)
        {
            string held = origin.Held is { } at
                ? $"holds through line {at}"
                : "holds none of its lines";
            string fault = origin.Fault is { } stopped
                ? $"; stopped: {stopped.Reason}"
                : string.Empty;
            lines.Add($"Origin {origin.Origin:D}: stored {origin.Stored} lines, {held}{fault}");
        }

        lines.AddRange(joined.Failures.Select(f => $"Not read: {f}"));
        lines.Add(joined.Trusted
            ? "This node's key is trusted in the project: the owner's vouch for it has arrived."
            : "This node's key is not yet trusted in the project: the owner's vouch for it has not arrived. The owner's node stores what this node records once that vouch is recorded.");
        return lines;
    }
}
