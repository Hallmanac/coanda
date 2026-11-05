using System.Net;
using System.Text.Json;
using Coanda.Cli;
using Coanda.Cli.Commands;
using Coanda.Daemon.Api.Endpoints;
using Coanda.Daemon.Modules;

namespace Coanda.Tests.Daemon.Modules;

/// <summary>
/// A module's settings over the loopback API, which names the module in the query or the body, and as
/// <c>coa project module show</c> renders them.
/// </summary>
public sealed class ModuleSettingsApiTests
{
    private const string On = ModuleNode.On;
    private const string Strict = ModuleNode.Module;
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task The_owner_sets_selects_and_shows_over_the_api_and_the_cli_renders_each_setting_and_the_state()
    {
        await using ModuleNode node = await ModuleSettingsTests.StartAsync();
        using var owner = new DaemonClient(node.Home);

        ModuleSettingsShown before = await owner.GetAsync<ModuleSettingsShown>(ProjectModuleEndpoints.ShowPathOf(On, Strict), None);
        Assert.Equal(
            [
                ("Project", "alpha"),
                ("Module", "test"),
                ("channel", "unset; required"),
                ("limit", "3 (the default)"),
                ("labels", "unset"),
                ("credential-profile", "none; required"),
                ("State", "on and unconfigured on this node; needs channel, credential-profile"),
            ],
            ProjectModuleShowCommand.ShowRows(before));

        await owner.PostAsync<ModuleSettingsShown>(
            $"{ProjectModuleEndpoints.Path}/settings", new ProjectModuleSetRequest(On, Strict, "channel", new ProjectModuleSetSettings { Project = On, Module = Strict, Name = "channel", Value = "#builds" }.ToValue()), None);
        ModuleSettingsTests.MakeProfile(node, Strict, "work");
        ModuleSettingsShown after = await owner.PostAsync<ModuleSettingsShown>(
            $"{ProjectModuleEndpoints.Path}/profile", new ProjectModuleSelectProfileRequest(On, Strict, "work"), None);

        Assert.Equal(
            [
                ("Project", "alpha"),
                ("Module", "test"),
                ("channel", "\"#builds\"; required"),
                ("limit", "3 (the default)"),
                ("labels", "unset"),
                ("credential-profile", "work; required"),
                ("State", "configured on this node"),
            ],
            ProjectModuleShowCommand.ShowRows(after));

        ModuleSettingsShown relaxed = await owner.GetAsync<ModuleSettingsShown>(ProjectModuleEndpoints.ShowPathOf(On, StandInModules.Relaxed), None);
        Assert.Contains(("credential-profile", "the node's default identity (the default); required"), ProjectModuleShowCommand.ShowRows(relaxed));
        Assert.Contains(("channel", "\"general\" (the default); required"), ProjectModuleShowCommand.ShowRows(relaxed));

        Directory.Delete(Path.Combine(node.Home.ModuleAreaOf(Strict), "work"));
        ModuleSettingsShown removed = await owner.GetAsync<ModuleSettingsShown>(ProjectModuleEndpoints.ShowPathOf(On, Strict), None);
        Assert.Contains(("credential-profile", "work, which this node no longer holds; required"), ProjectModuleShowCommand.ShowRows(removed));
        Assert.Contains(("State", "on and unconfigured on this node; needs credential-profile"), ProjectModuleShowCommand.ShowRows(removed));
    }

    [Fact]
    public async Task A_module_token_sets_and_shows_its_own_settings_and_is_refused_a_profile_and_another_modules_settings()
    {
        await using ModuleNode node = await ModuleSettingsTests.StartAsync();
        string token = await node.TokenAsync(Strict);
        ModuleSettingsTests.MakeProfile(node, Strict, "work");

        (HttpStatusCode set, string body) = await node.SendAsync(
            token, HttpMethod.Post, $"{ProjectModuleEndpoints.Path}/settings", new ProjectModuleSetRequest(On, Strict, "labels", StandInModules.Json("""["x"]""")));
        Assert.True(set == HttpStatusCode.OK, $"The module's own set was answered {set}: {body}");
        using var module = new DaemonClient(node.Home, token);
        ModuleSettingsShown shown = await module.GetAsync<ModuleSettingsShown>(ProjectModuleEndpoints.ShowPathOf(On, Strict), None);
        Assert.Equal("[\"x\"]", shown.Replicated.Single(s => s.Name == "labels").Value?.GetRawText());

        Assert.Equal(
            ModuleNode.NotOpen("project-module.select-profile"),
            await node.RefusedAsync(token, HttpMethod.Post, $"{ProjectModuleEndpoints.Path}/profile", new ProjectModuleSelectProfileRequest(On, Strict, "work")));
        Assert.Equal(
            "Module 'test' sets and shows only its own settings, not those of module 'other'.",
            await node.RefusedAsync(token, HttpMethod.Get, ProjectModuleEndpoints.ShowPathOf(On, ModuleNode.Other)));
        Assert.Equal(
            ModuleNode.NotSwitchedOn(Strict, ModuleNode.Off),
            await node.RefusedAsync(token, HttpMethod.Post, $"{ProjectModuleEndpoints.Path}/settings", new ProjectModuleSetRequest(ModuleNode.Off, Strict, "channel", StandInModules.Json("\"#x\""))));
    }

    [Theory]
    [InlineData("main", "\"main\"")]
    [InlineData("\"main\"", "\"main\"")]
    [InlineData("3", "3")]
    [InlineData("true", "true")]
    [InlineData("[\"a\", \"b\"]", "[\"a\", \"b\"]")]
    [InlineData("{\"build\": \"dotnet build\"}", "{\"build\": \"dotnet build\"}")]
    [InlineData("#builds", "\"#builds\"")]
    public void The_cli_reads_a_value_as_json_when_it_is_json_and_as_text_otherwise(string given, string sent)
    {
        JsonElement value = new ProjectModuleSetSettings { Project = On, Module = Strict, Name = "x", Value = given }.ToValue();

        Assert.Equal(sent, value.GetRawText());
    }
}
