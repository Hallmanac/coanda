using Coanda.Daemon;
using Coanda.Tests.Daemon;

namespace Coanda.Tests;

internal static class NodeHomeClones
{
    /// <summary>The core's clone of <paramref name="repo"/> in <paramref name="project"/>, the repo every test registers when none is named.</summary>
    public static string ProjectClonePath(this NodeHome home, string project, string repo = RegisteredProjects.Repo) => home.RepoClonePath(project, repo);
}
