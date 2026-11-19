using System.Text.RegularExpressions;
using Coanda.Cli;
using Coanda.Cli.Commands;
using Coanda.Daemon.Api;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Tests.Daemon;

namespace Coanda.Tests.Cli;

/// <summary>
/// <c>coa parcel add</c> compiles in no default type: given no <c>--type</c>, it creates nothing and names
/// the parcel types the project holds, the ones <c>coa type list</c> shows, so the person can pick one.
/// </summary>
public sealed partial class ParcelAddCommandTests
{
    private const string Project = "typed";
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task Given_no_type_it_creates_no_parcel_and_names_the_types_the_project_holds()
    {
        using var temp = new TemporaryHome();
        await using RunningDaemon daemon = await Daemons.StartAsync(temp.Home, None);
        using var client = new DaemonClient(temp.Home);
        Ran registered = await CoaProcess.RunAsync(temp.Home, None, "project", "add", Project, "--repo", "git@example.invalid:typed.git");
        Assert.True(registered.ExitCode == 0, registered.Output + registered.Error);
        string[] types = [.. (await client.GetAsync<List<TypeView>>($"/types?project={Project}", None)).Select(type => type.Name)];

        Ran refused = await CoaProcess.RunAsync(temp.Home, None, "parcel", "add", "--project", Project, "--title", "Which type?");

        Assert.NotEmpty(types);
        Assert.NotEqual(0, refused.ExitCode);
        Assert.Equal(Squashed(ParcelAddCommand.NoTypeRefusal(Project, types)), Squashed(refused.Output + refused.Error));
        Assert.Contains("--type", Squashed(refused.Output), StringComparison.Ordinal);
        Assert.All(types, type => Assert.Contains(type, Squashed(refused.Output), StringComparison.Ordinal));
        Assert.Empty(await client.GetAsync<List<ParcelView>>($"/parcels?project={Project}", None));

        await daemon.StopAsync(None);
    }

    [Fact]
    public void The_refusal_says_to_name_the_type_and_names_each_type()
    {
        Assert.Equal(
            "Name the parcel's type with --type. The project p holds alpha, beta. No parcel was created.",
            ParcelAddCommand.NoTypeRefusal("p", ["alpha", "beta"]));
        Assert.Equal(
            "Name the parcel's type with --type. The project p holds no parcel type yet, so no parcel was created.",
            ParcelAddCommand.NoTypeRefusal("p", []));
    }

    /// <summary>The text with every escape sequence and all white space removed, since the CLI wraps and may colour what it prints.</summary>
    private static string Squashed(string text) => Whitespace().Replace(Escapes().Replace(text, string.Empty), string.Empty);

    [GeneratedRegex(@"\x1b\[[0-9;]*[A-Za-z]")]
    private static partial Regex Escapes();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
