using Coanda.Daemon.Executors;

namespace Coanda.Daemon.Modules;

/// <summary>
/// The skills of every module whose directory the daemon read at start, held under the module name
/// each directory's manifest gives, so a module switched on for a project by that name brings them.
/// An agent's named skill is resolved here, among the folders read, and never joined onto a path.
/// </summary>
public sealed class ModuleSkills
{
    private readonly Lock gate = new();
    private readonly Dictionary<string, IReadOnlyList<ModuleSkill>> byModule = new(StringComparer.Ordinal);

    /// <summary>Holds the skills <paramref name="artifact"/> ships under its module's name, in place of any held for it before.</summary>
    public void Hold(ModuleArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        lock (gate)
        {
            byModule[artifact.Module] = artifact.Skills;
        }
    }

    /// <summary>The skills held for <paramref name="module"/>, or none.</summary>
    public IReadOnlyList<ModuleSkill> Of(string module)
    {
        lock (gate)
        {
            return byModule.TryGetValue(module, out IReadOnlyList<ModuleSkill>? skills) ? skills : [];
        }
    }

    /// <summary>
    /// The skills <paramref name="agent"/> names, in the order it names them, each the folder of
    /// exactly that name in the skills folder of the one module of <paramref name="switchedOn"/> that
    /// ships it; or, with the reason in <paramref name="refusal"/>, null when two names differ only
    /// in letter case, when no module switched on ships a name, or when two of them do.
    /// </summary>
    public IReadOnlyList<SessionSkill>? Resolve(
        string agent, IReadOnlyList<string> named, string project, IReadOnlyList<string> switchedOn, out string? refusal)
    {
        ArgumentNullException.ThrowIfNull(named);
        ArgumentNullException.ThrowIfNull(switchedOn);
        if (named.GroupBy(n => n, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Distinct(StringComparer.Ordinal).Count() > 1) is { } cased)
        {
            string[] names = [.. cased.Distinct(StringComparer.Ordinal)];
            refusal = $"Agent '{agent}' names the skills {Listed(names)}, which differ only in letter case, so a session could not hold both.";
            return null;
        }

        List<SessionSkill> resolved = [];
        foreach (string name in named)
        {
            (string Module, ModuleSkill Skill)[] shipping = [.. switchedOn
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .SelectMany(module => Of(module).Where(s => string.Equals(s.Name, name, StringComparison.Ordinal)).Select(s => (module, s)))];
            switch (shipping)
            {
                case []:
                    refusal = $"Agent '{agent}' names the skill '{name}', which no module switched on for project '{project}' ships.";
                    return null;
                case [var only]:
                    resolved.Add(new SessionSkill(name, only.Module, only.Skill.Directory));
                    break;
                default:
                    refusal = $"Agent '{agent}' names the skill '{name}', which the modules {Listed([.. shipping.Select(s => s.Module)])} switched on for project '{project}' each ship, so which one is meant is not known.";
                    return null;
            }
        }

        refusal = null;
        return resolved;
    }

    private static string Listed(IReadOnlyList<string> names) =>
        string.Join(" and ", names.Select(n => $"'{n}'"));
}
