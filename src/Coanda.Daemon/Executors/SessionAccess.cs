using System.Text;

namespace Coanda.Daemon.Executors;

/// <summary>
/// What a judgment's session needs to reach the daemon that spawned it through <c>coa</c>: the
/// session token issued for its launch, the node home, and the flow's project. An adapter places
/// each in the session's environment under the variable this type names for it, so <c>coa</c> run
/// inside the session reaches that daemon and that project without flags. The token is a secret:
/// this type's printed form leaves it out.
/// </summary>
public sealed record SessionAccess(string Token, string NodeHome, string Project)
{
    /// <summary>The variable <c>coa</c> reads the session token from; when set, it presents that token instead of the daemon token.</summary>
    public const string TokenVariable = "COANDA_SESSION_TOKEN";

    /// <summary>The variable <c>coa</c> reads its default project from.</summary>
    public const string ProjectVariable = "COANDA_PROJECT";

    /// <summary>The variables a session's environment holds, by name.</summary>
    public IReadOnlyDictionary<string, string> Variables => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [TokenVariable] = Token,
        [Daemon.NodeHome.EnvironmentVariable] = NodeHome,
        [ProjectVariable] = Project,
    };

    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append("Token = (withheld), NodeHome = ").Append(NodeHome).Append(", Project = ").Append(Project);
        return true;
    }
}
