namespace Coanda.Tests.Sdk;

/// <summary>Files of the repository the tests run from, found by walking up from the test's output to the solution file.</summary>
public static class RepositoryFiles
{
    private static readonly Lazy<string> RootPath = new(FindRoot);

    public static string Root => RootPath.Value;

    /// <summary>The full path of <paramref name="relative"/>, a path from the repository's root written with forward slashes.</summary>
    public static string PathOf(string relative) =>
        Path.Combine([Root, .. relative.Split('/')]);

    private static string FindRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Coanda.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"No Coanda.slnx above {AppContext.BaseDirectory}, so the repository's files cannot be found.");
    }
}
