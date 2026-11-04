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

        printed.Should().Contain("task[beta]").And.Contain("new, [paused]");
    }

    [Fact]
    public void The_contour_list_prints_a_name_holding_square_brackets_as_itself()
    {
        var contour = new ContourView("build[red]", 2, "0123456789abcdef");

        string printed = Render(DefinitionRendering.ContourListColumns, DefinitionRendering.ContourListRow(contour));

        printed.Should().Contain("build[red]");
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
