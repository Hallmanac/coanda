namespace Coanda.Daemon.FlowRuns;

/// <summary>
/// The stored binding of one session token: the SHA-256 hash of the token, never its plain value,
/// and the project, flow run, and launch it was issued for. It lives in the node tenant, since a
/// presented token does not say which project it belongs to. No API view returns it.
/// </summary>
public sealed class SessionTokenRecord
{
    /// <summary>The token's SHA-256 hash, lower-case hex.</summary>
    public string Id { get; set; } = string.Empty;

    public string Project { get; set; } = string.Empty;

    public Guid FlowRunId { get; set; }

    public Guid LaunchId { get; set; }
}
