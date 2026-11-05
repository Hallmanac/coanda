using Coanda.Cli.Commands;
using Coanda.Daemon.Api.Endpoints;
using Spectre.Console;

namespace Coanda.Tests.Cli;

public sealed class DaemonStatusRenderingTests
{
    [Fact]
    public void Status_escapes_the_version_home_and_database_the_daemon_sends_so_they_render_as_text()
    {
        var status = new DaemonStatusView(42, 5000, DateTimeOffset.UnixEpoch, "1.0[beta]", "/tmp/run[1]", "/tmp/run[1]/coanda.db");

        IReadOnlyList<(string Key, string Value)> rows = DaemonStatusCommand.StatusRows(status);

        Assert.Equal("1.0[[beta]]", rows.Single(r => r.Key == "Version").Value);
        Assert.Equal("/tmp/run[[1]]", rows.Single(r => r.Key == "Home").Value);
        Assert.Equal("/tmp/run[[1]]/coanda.db", rows.Single(r => r.Key == "Database").Value);
        string rendered = string.Concat(Render(rows).Where(c => !char.IsWhiteSpace(c)));
        Assert.Contains("Home/tmp/run[1]", rendered);
        Assert.Contains("Database/tmp/run[1]/coanda.db", rendered);
    }

    private static string Render(IReadOnlyList<(string Key, string Value)> rows)
    {
        using var writer = new StringWriter();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings { Ansi = AnsiSupport.No, Out = new AnsiConsoleOutput(writer) });
        var table = new Table().HideHeaders().NoBorder().AddColumns("k", "v");
        foreach ((string key, string value) in rows)
        {
            table.AddRow(key, value);
        }

        console.Write(table);
        return writer.ToString();
    }
}
