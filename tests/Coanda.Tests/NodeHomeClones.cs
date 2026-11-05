using Coanda.Daemon;
using Coanda.Modules.Software.Repositories;

namespace Coanda.Tests;

internal static class NodeHomeClones
{
    /// <summary>The clone layout the software module reads from the home, built the way the daemon hands it the paths.</summary>
    public static ClonePaths Clones(this NodeHome home) => new(home.ClonesPath, home.WorkPath);
}
