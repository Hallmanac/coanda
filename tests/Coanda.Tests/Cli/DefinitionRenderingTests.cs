using Coanda.Cli.Commands;
using Coanda.Daemon.Api.Endpoints;
using Spectre.Console;

namespace Coanda.Tests.Cli;

public sealed class DefinitionRenderingTests
{
    [Fact]
    public void The_type_list_prints_a_name_holding_square_brackets_as_itself()
    {
        var type = new TypeView("task[beta]", "0123456789abcdef", ["new", "[paused]"], ["completed"]);

        string printed = Render(DefinitionRendering.TypeListColumns, DefinitionRendering.TypeListRow(type));

        Assert.Contains("task[beta]", printed, StringComparison.Ordinal);
        Assert.Contains("new, [paused]", printed, StringComparison.Ordinal);
    }

    [Fact]
    public void The_flow_list_prints_a_name_holding_square_brackets_as_itself()
    {
        var flow = new FlowView("build[red]", 2, "0123456789abcdef");

        string printed = Render(DefinitionRendering.FlowListColumns, DefinitionRendering.FlowListRow(flow));

        Assert.Contains("build[red]", printed, StringComparison.Ordinal);
    }

    private static string Render(string[] columns, string[] row)
    {
        using var output = new StringWriter();
        IAnsiConsole console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(output),
        });
        console.Profile.Width = 200;

        console.Write(new Table().AddColumns(columns).AddRow(row));
        return output.ToString();
    }
}
