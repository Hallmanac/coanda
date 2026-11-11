using System.Reflection;
using System.Text.Json;
using Coanda.Modules.Software.Steps;
using Coanda.Sdk.Contract;

namespace Coanda.Modules.Software;

/// <summary>
/// The software module's describe answer: its five steps, each with the schema of the arguments a flow
/// author writes for it (none, today) and of the result its done answer carries, no external event
/// kinds, its subscriptions to the ends and stops of flow runs, whose worktrees it removes, and its
/// settings schema. The settings schema is the one copy of the
/// module's settings: the daemon checks the settings a project records for the module against this.
/// </summary>
public static class SoftwareDescribe
{
    /// <summary>The module's version, which its describe answer names.</summary>
    public static string Version =>
        typeof(SoftwareDescribe).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    /// <summary>
    /// The module's settings for each project it is switched on for. The replicated settings name each
    /// repo's primary branch, as an object from a repo's name to its branch; the build and test commands,
    /// which default to <see cref="SoftwareSettings.DefaultBuild"/> and <see cref="SoftwareSettings.DefaultTest"/>;
    /// and the checks a merge waits for. None is required, so a project missing one is still configured. A
    /// step that needs a primary branch for its repo pauses naming the setting when it has none; the
    /// build and test commands fall back to their defaults; and a merge with no checks named waits on every
    /// check reported on the head instead, under the settle rule <see cref="GatedMergeStep"/> states for a
    /// head with none reported, so naming checks is how a project makes it wait for a check that has not
    /// been reported by the time every reported one has finished. A branch, a command, and a check must each hold
    /// something other than white space. The credential profile setting names the profile a project's steps
    /// read their GitHub token through; it is required, and a project that selects none acts as the account
    /// the node's GitHub CLI is signed in to, which counts as its value.
    /// </summary>
    public static SettingsSchema Settings { get; } = new(
        [
            new ReplicatedSetting(
                SoftwareSettings.PrimaryBranch,
                Json("""{ "type": "object", "minProperties": 1, "additionalProperties": { "type": "string", "pattern": "\\S" } }"""),
                Required: false),
            new ReplicatedSetting(
                SoftwareSettings.Build, Json("""{ "type": "string", "pattern": "\\S" }"""), Required: false, Default: JsonSerializer.SerializeToElement(SoftwareSettings.DefaultBuild)),
            new ReplicatedSetting(
                SoftwareSettings.Test, Json("""{ "type": "string", "pattern": "\\S" }"""), Required: false, Default: JsonSerializer.SerializeToElement(SoftwareSettings.DefaultTest)),
            new ReplicatedSetting(
                SoftwareSettings.RequiredChecks,
                Json("""{ "type": "array", "minItems": 1, "items": { "type": "string", "pattern": "\\S" } }"""),
                Required: false),
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
        [CoreEventKind.FlowRunEnded, CoreEventKind.FlowRunStopped],
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
