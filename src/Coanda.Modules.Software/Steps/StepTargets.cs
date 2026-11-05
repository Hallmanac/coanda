using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Coanda.Modules.Software.Repositories;
using Coanda.Sdk.Client;
using Coanda.Sdk.Contract;

namespace Coanda.Modules.Software.Steps;

/// <summary>
/// What a step that acts on a repo acts on: the project, the repo's name and remote, and the repo's
/// primary branch, which the module's settings name.
/// </summary>
public sealed record RepoTarget(string Project, string Repo, string Remote, string PrimaryBranch);

/// <summary>What a step read before acting: the value, or the pause it answers instead when it could not read one.</summary>
public sealed class Needed<T>
    where T : class
{
    private readonly T? value;
    private readonly DispatchAnswer? pause;

    private Needed(T? value, DispatchAnswer? pause)
    {
        this.value = value;
        this.pause = pause;
    }

    public static Needed<T> Ready(T value) => new(value ?? throw new ArgumentNullException(nameof(value)), null);

    public static Needed<T> Paused(DispatchAnswer pause) => new(null, pause ?? throw new ArgumentNullException(nameof(pause)));

    /// <summary>Whether the step lacks the value and answers <paramref name="answer"/> instead; when it does not, <paramref name="found"/> is the value.</summary>
    public bool Lacks([NotNullWhen(false)] out T? found, [NotNullWhen(true)] out DispatchAnswer? answer)
    {
        if (pause is { } paused)
        {
            (found, answer) = (null, paused);
            return true;
        }

        (found, answer) = (value ?? throw new InvalidOperationException("A needed value holds neither a value nor a pause."), null);
        return false;
    }
}

/// <summary>
/// How the module's steps find what they act on: the repo the task works in, which the task's
/// <see cref="SoftwareSettings.ReposField"/> field names, and the module's settings in the project. A
/// step that needs either and cannot have it pauses with a lever saying what to set.
/// </summary>
public static class StepTargets
{
    /// <summary>
    /// The repo the task works in and its primary branch. The repo is the one the task's repos field
    /// names; a task whose field is empty in a project with one repo has the field set to that repo first,
    /// as the module that owns it, so a later step on the task acts on the same repo; and a parcel whose type
    /// version declares no repos field acts on the project's only repo. A project with more than one repo
    /// and an empty field, a field naming a repo the project does not have, and a field naming more than
    /// one repo each pause with a lever that names the field. A repo with no primary branch in the
    /// module's settings pauses with a lever that names the setting.
    /// </summary>
    public static async Task<Needed<RepoTarget>> TargetAsync(StepContext context, CoreProject project, CancellationToken cancellationToken)
    {
        if ((await RepoAsync(context, project, cancellationToken)).Lacks(out CoreRepo? chosen, out DispatchAnswer? pause))
        {
            return Needed<RepoTarget>.Paused(pause);
        }

        CoreModuleSettings settings = await context.SettingsAsync(cancellationToken);
        return settings.ValueOf(SoftwareSettings.PrimaryBranch) is { ValueKind: JsonValueKind.Object } branches
            && branches.TryGetProperty(chosen.Name, out JsonElement branch)
            && branch.ValueKind == JsonValueKind.String
            && branch.GetString() is { } named
            && !string.IsNullOrWhiteSpace(named)
                ? Needed<RepoTarget>.Ready(new RepoTarget(project.Name, chosen.Name, chosen.Remote, named))
                : Needed<RepoTarget>.Paused(await MissingSettingAsync(
                    context,
                    SoftwareSettings.PrimaryBranch,
                    $"has no primary branch for repo '{chosen.Name}': the software module's setting '{SoftwareSettings.PrimaryBranch}' names none for it in project '{project.Name}'.",
                    $"'{{\"{chosen.Name}\":\"<branch>\"}}'",
                    cancellationToken));
    }

    /// <summary>The repo the task works in, as <see cref="TargetAsync"/> finds it.</summary>
    public static async Task<Needed<CoreRepo>> RepoAsync(StepContext context, CoreProject project, CancellationToken cancellationToken)
    {
        CoreParcel parcel = await context.ParcelAsync(cancellationToken);
        CoreParcelField? field = parcel.Fields.FirstOrDefault(f => string.Equals(f.Name, SoftwareSettings.ReposField, StringComparison.Ordinal));
        IReadOnlyList<CoreRepo> repos = project.Repos;
        string held = string.Join(", ", repos.Select(r => $"'{r.Name}'"));
        if (field is null)
        {
            return repos.Count == 1
                ? Needed<CoreRepo>.Ready(repos[0])
                : Needed<CoreRepo>.Paused(await context.PausedAsync(
                    $"has no repo to act on: project '{project.Name}' has {Count(repos.Count)} ({held}), and parcel {ShortId.Of(parcel.Id)}'s type version declares no '{SoftwareSettings.ReposField}' field to name one.",
                    Levers.NoReposField,
                    cancellationToken));
        }

        IReadOnlyList<string> named = field.Value?.Items ?? [];
        switch (named.Count)
        {
            case 0 when repos.Count == 1:
                await context.SetOwnedFieldAsync(SoftwareSettings.ReposField, new CoreFieldValue(Items: [repos[0].Name]), cancellationToken);
                return Needed<CoreRepo>.Ready(repos[0]);
            case 0:
                return await NameRepoAsync(
                    $"has no repo to act on: project '{project.Name}' has {Count(repos.Count)} ({held}), and the task's '{SoftwareSettings.ReposField}' field names none.");
            case > 1:
                return await NameRepoAsync(
                    $"acts on one repo, and the task's '{SoftwareSettings.ReposField}' field names {named.Count}: {string.Join(", ", named.Select(n => $"'{n}'"))}.");
        }

        return repos.FirstOrDefault(r => string.Equals(r.Name, named[0], StringComparison.Ordinal)) is { } found
            ? Needed<CoreRepo>.Ready(found)
            : await NameRepoAsync(
                $"has no repo to act on: the task's '{SoftwareSettings.ReposField}' field names '{named[0]}', and project '{project.Name}' has no such repo; it has {held}.");

        async Task<Needed<CoreRepo>> NameRepoAsync(string what) =>
            Needed<CoreRepo>.Paused(await context.PausedAsync(what, step => Levers.NameRepo(step, project.Name, ShortId.Of(parcel.Id), [.. repos.Select(r => r.Name)]), cancellationToken));
    }

    /// <summary>
    /// The value of the module's setting <paramref name="name"/> in the project, its default included,
    /// or null when it has none.
    /// </summary>
    public static async Task<JsonElement?> SettingAsync(StepContext context, string name, CancellationToken cancellationToken) =>
        (await context.SettingsAsync(cancellationToken)).ValueOf(name);

    /// <summary>A pause for a setting the step needs that has no value and no default, whose lever names the setting and the command that sets it.</summary>
    public static Task<DispatchAnswer> MissingSettingAsync(
        StepContext context, string name, string what, string example, CancellationToken cancellationToken) =>
        context.PausedAsync(what, step => Levers.SetSetting(step, context.Request.Project, name, example), cancellationToken);

    private static string Count(int repos) => repos == 1 ? "1 repo" : $"{repos} repos";
}
