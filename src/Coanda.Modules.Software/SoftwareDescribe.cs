using System.Reflection;
using System.Text.Json;
using Coanda.Modules.Software.Steps;
using Coanda.Sdk.Contract;

namespace Coanda.Modules.Software;

/// <summary>
/// The software module's describe answer: its five steps, each with the schema of the arguments a flow
/// author writes for it (none, today) and of the result its done answer carries, no external event
/// kinds and no subscriptions yet, and its settings schema. The settings schema is the one copy of the
/// module's settings: the daemon checks the settings a project records for the module against this.
/// </summary>
public static class SoftwareDescribe
{
    /// <summary>The module's version, which its describe answer names.</summary>
    public static string Version =>
        typeof(SoftwareDescribe).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    /// <summary>
    /// The module's settings for each project it is switched on for. The replicated settings name the
    /// repository's primary branch, the build and test commands, and the checks a merge waits for, none
    /// of them required and none with a default. The credential profile setting names the GitHub account
    /// a project's steps act as; it is required, and a project that selects none acts as the account the
    /// node's GitHub CLI is signed in to, which counts as its value.
    /// </summary>
    public static SettingsSchema Settings { get; } = new(
        [
            new ReplicatedSetting("primary-branch", Json("""{ "type": "string", "minLength": 1 }"""), Required: false),
            new ReplicatedSetting("build", Json("""{ "type": "string", "minLength": 1 }"""), Required: false),
            new ReplicatedSetting("test", Json("""{ "type": "string", "minLength": 1 }"""), Required: false),
            new ReplicatedSetting(
                "required-checks", Json("""{ "type": "array", "minItems": 1, "items": { "type": "string", "minLength": 1 } }"""), Required: false),
        ],
        new CredentialProfileSetting(Required: true, DefaultsToNodeIdentity: true));

    /// <summary>The arguments every one of the module's steps takes today: none.</summary>
    public static JsonElement NoArguments { get; } = Json("""{ "type": "object", "additionalProperties": false, "properties": {} }""");

    public static DescribeAnswer Answer { get; } = new(
        ModuleApi.Version,
        Version,
        [
            new StepDescription(SoftwareSteps.Worktree, NoArguments, Result(SoftwareSteps.Worktree, Results.DoneVerdict, """
                "branch": { "type": "string", "minLength": 1 },
                "head": { "type": "string", "minLength": 1 }
                """)),
            new StepDescription(SoftwareSteps.Gates, NoArguments, OneOf(
                Result(SoftwareSteps.Gates, Results.DoneVerdict, """
                    "build": { "type": "string", "minLength": 1 },
                    "test": { "type": "string", "minLength": 1 }
                    """),
                Result(SoftwareSteps.Gates, Results.FailedVerdict, """
                    "reason": { "type": "string", "minLength": 1 }
                    """))),
            new StepDescription(SoftwareSteps.OpenPr, NoArguments, Result(SoftwareSteps.OpenPr, OpenPrStep.OpenedVerdict, """
                "number": { "type": "integer", "minimum": 1 },
                "url": { "type": "string", "minLength": 1 },
                "head": { "type": "string", "minLength": 1 }
                """)),
            new StepDescription(SoftwareSteps.GatedMerge, NoArguments, OneOf(
                Result(SoftwareSteps.GatedMerge, GatedMergeStep.MergedVerdict, """
                    "commit": { "type": "string", "minLength": 1 }
                    """),
                Result(SoftwareSteps.GatedMerge, GatedMergeStep.PendingVerdict, """
                    "reason": { "type": "string", "minLength": 1 }
                    """),
                Result(SoftwareSteps.GatedMerge, GatedMergeStep.BehindVerdict, """
                    "reason": { "type": "string", "minLength": 1 },
                    "behind": { "type": "integer", "minimum": 1 }
                    """))),
            new StepDescription(SoftwareSteps.Rebase, NoArguments, Result(SoftwareSteps.Rebase, Results.DoneVerdict, """
                "branch": { "type": "string", "minLength": 1 },
                "head": { "type": "string", "minLength": 1 }
                """)),
        ],
        [],
        [],
        Settings);

    /// <summary>
    /// The schema of one result: an object with exactly the verdict <paramref name="verdict"/>, the step's
    /// name under <c>systemStep</c>, and the further <paramref name="properties"/>, all of them required.
    /// </summary>
    private static JsonElement Result(string step, string verdict, string properties)
    {
        using JsonDocument further = JsonDocument.Parse($"{{ {properties} }}");
        string[] required = ["verdict", "systemStep", .. further.RootElement.EnumerateObject().Select(p => p.Name)];
        return Json($$"""
            {
              "type": "object",
              "additionalProperties": false,
              "required": {{JsonSerializer.Serialize(required)}},
              "properties": {
                "verdict": { "type": "string", "enum": [{{JsonSerializer.Serialize(verdict)}}] },
                "systemStep": { "type": "string", "enum": [{{JsonSerializer.Serialize(step)}}] },
                {{properties}}
              }
            }
            """);
    }

    private static JsonElement OneOf(params JsonElement[] results) =>
        Json($$"""{ "oneOf": [{{string.Join(", ", results.Select(r => r.GetRawText()))}}] }""");

    private static JsonElement Json(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
