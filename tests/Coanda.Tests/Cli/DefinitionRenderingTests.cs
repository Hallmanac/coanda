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

    [Fact]
    public void A_definition_row_gives_its_short_id_and_names_a_module_scope_ones_module_and_version()
    {
        var module = new DefinitionView(
            Guid.Parse("0192a3b4-0000-7000-8000-00000000abcd"), "flow", "build[red]", "module", null, "software", "0.4.0", 2, "published", "0123456789abcdef", null);
        var project = module with { Scope = "project", Project = "alpha", Module = null, ModuleVersion = null, Status = "draft" };

        string printed = Render(DefinitionRendering.DefinitionListColumns, DefinitionRendering.DefinitionListRow(module));

        Assert.Equal(["0000abcd", "flow", "build[[red]]", "module (software 0.4.0)", "2", "published", "0123456789abcdef"], DefinitionRendering.DefinitionListRow(module));
        Assert.Contains("build[red]", printed, StringComparison.Ordinal);
        Assert.Equal("project (alpha)", DefinitionRendering.ScopeOf(project));
        Assert.Equal("owner", DefinitionRendering.ScopeOf(project with { Scope = "owner", Project = null }));
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
