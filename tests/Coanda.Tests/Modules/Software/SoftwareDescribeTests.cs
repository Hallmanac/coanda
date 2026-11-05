using System.Text.Json;
using Coanda.Core.Flows;
using Coanda.Daemon.Modules;
using Coanda.Modules.Software;
using Coanda.Sdk.Contract;
using Coanda.Tests.Sdk.Contract;
using Microsoft.Extensions.DependencyInjection;
using NJsonSchema;

namespace Coanda.Tests.Modules.Software;

/// <summary>
/// The software module's describe answer: a document the describe schema accepts, listing the five
/// steps, each with an argument schema and a result schema, and the module's settings schema, whose
/// step names are the ones the shipped build and merge flows name and whose argument schemas accept
/// the arguments those flows give.
/// </summary>
public sealed class SoftwareDescribeTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task The_describe_answer_meets_the_describe_schema()
    {
        await using SoftwareNode node = await SoftwareNode.StartAsync();
        DescribeAnswer answer = HostedAnswer(node);

        string written = JsonSerializer.Serialize(answer, ModuleJson.Options);

        Assert.Empty(ModuleSchemas.Load("describe.schema.json").Validate(written));
    }

    [Fact]
    public void The_answer_lists_the_five_steps_each_with_an_argument_and_a_result_schema_and_the_settings_schema()
    {
        DescribeAnswer answer = SoftwareDescribe.Answer;

        Assert.Equal(
            ["software/worktree", "software/gates", "software/open-pr", "software/gated-merge", "software/rebase"],
            answer.Steps.Select(s => s.Name));
        Assert.All(answer.Steps, step =>
        {
            Assert.Equal(JsonValueKind.Object, step.ArgumentSchema.ValueKind);
            Assert.Equal(JsonValueKind.Object, step.ResultSchema.ValueKind);
        });
        Assert.Same(SoftwareDescribe.Settings, answer.SettingsSchema);
        Assert.Equal(["primary-branch", "build", "test", "required-checks"], answer.SettingsSchema.Replicated.Select(s => s.Name));
        Assert.Equal(new CredentialProfileSetting(Required: true, DefaultsToNodeIdentity: true), answer.SettingsSchema.CredentialProfile);
    }

    [Fact]
    public async Task The_shipped_build_and_merge_flows_name_the_software_steps_as_describe_does_and_give_each_arguments_its_schema_accepts()
    {
        Dictionary<string, StepDescription> described = SoftwareDescribe.Answer.Steps.ToDictionary(s => s.Name, StringComparer.Ordinal);
        FlowStep[] software = [.. new[] { SoftwareFlows.Build, SoftwareFlows.Merge }
            .SelectMany(f => f.Steps)
            .Where(s => s.SystemStep is { } name && !CoreSystemSteps.All.Contains(name))];

        Assert.NotEmpty(software);
        Assert.Equivalent(SoftwareSteps.All, software.Select(s => s.SystemStep).Distinct());
        foreach (FlowStep step in software)
        {
            StepDescription description = described[step.SystemStep ?? string.Empty];
            JsonSchema schema = await JsonSchema.FromJsonAsync(description.ArgumentSchema.GetRawText(), None);
            string arguments = JsonSerializer.Serialize(step.Arguments);

            Assert.Equal("{}", arguments);
            Assert.Empty(schema.Validate(arguments));
        }
    }

    [Fact]
    public async Task An_argument_a_step_does_not_take_is_refused_by_its_argument_schema()
    {
        JsonSchema schema = await JsonSchema.FromJsonAsync(SoftwareDescribe.NoArguments.GetRawText(), None);

        Assert.NotEmpty(schema.Validate("""{ "stage": "review" }"""));
    }

    /// <summary>What the module the daemon hosts answered to describe when the daemon started.</summary>
    private static DescribeAnswer HostedAnswer(SoftwareNode node) =>
        node.Services.GetRequiredService<HostedModules>().DescribeOf(SoftwareSteps.Module)
            ?? throw new InvalidOperationException("The daemon hosts no software module.");
}
