using Coanda.Core;
using Coanda.Core.Registry;

namespace Coanda.Tests.Core;

/// <summary>
/// The registry opens the Module role to the reads a module needs, to recording external events, to
/// the two writes it makes under a dispatch it holds or on a field its module owns, and to setting and
/// showing its own settings, and to nothing else; every other role is open to what it was open to before
/// the Module role existed, or for an entry added since, what it was added open to, and only the owner
/// switches modules on.
/// </summary>
public sealed class ModuleRoleRegistryTests
{
    private static readonly string[] OpenToModules =
    [
        "project.show", "project.list",
        "project-module.set", "project-module.show",
        "parcel.list", "parcel.show", "parcel.set-field",
        "type.list", "flow.list", "agent.list",
        "flow-run.list", "flow-run.show", "flow-run.outcome",
        "event.record", "subscription.list",
    ];

    private static readonly Role[] Owner = [Role.Owner];
    private static readonly Role[] People = [Role.Owner, Role.Member];
    private static readonly Role[] Everyone = [Role.Owner, Role.Member, Role.Agent];

    /// <summary>The roles other than Module each entry is open to, by name: for an entry older than the Module role, those it was open to before it.</summary>
    private static readonly Dictionary<string, Role[]> Before = new(StringComparer.Ordinal)
    {
        ["project.add"] = Owner,
        ["project.set-profile"] = Owner,
        ["project-module.set"] = Owner,
        ["project-module.select-profile"] = Owner,
        ["project-module.show"] = Everyone,
        ["project.show"] = Everyone,
        ["project.list"] = Everyone,
        ["parcel.list"] = Everyone,
        ["parcel.show"] = Everyone,
        ["parcel.add"] = People,
        ["parcel.move"] = Everyone,
        ["parcel.set-field"] = Everyone,
        ["parcel.close"] = People,
        ["parcel.claim"] = People,
        ["type.list"] = Everyone,
        ["flow.list"] = Everyone,
        ["agent.list"] = Everyone,
        ["flow-run.list"] = Everyone,
        ["flow-run.show"] = Everyone,
        ["flow-run.start"] = Everyone,
        ["flow-run.step"] = Everyone,
        ["flow-run.respawn"] = Everyone,
        ["flow-run.spawn"] = Everyone,
        ["flow-run.session"] = Everyone,
        ["flow-run.answer"] = Everyone,
        ["flow-run.usage"] = Everyone,
        ["flow-run.result"] = Everyone,
        ["flow-run.dispatch"] = Everyone,
        ["flow-run.outcome"] = Everyone,
        ["flow-run.pause"] = Everyone,
        ["flow-run.resume"] = People,
        ["flow-run.end"] = Everyone,
        ["flow-run.stop"] = People,
        ["flow-run.yield"] = [Role.Agent],
        ["event.record"] = People,
        ["subscription.add"] = People,
        ["subscription.list"] = Everyone,
        ["definition.draft"] = Everyone,
        ["definition.publish"] = Owner,
        ["definition.retire"] = Owner,
        ["daemon.status"] = Everyone,
        ["daemon.stop"] = People,
    };

    [Fact]
    public void The_module_role_is_open_to_exactly_the_reads_the_event_record_the_two_field_writes_and_its_own_settings()
    {
        IEnumerable<string> open = CommandRegistry.Entries.Where(e => e.AllowedRoles.Contains(Role.Module)).Select(e => e.Name);

        Assert.Equal(OpenToModules.Order(StringComparer.Ordinal), open.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_other_entry_refuses_a_module_principal_with_the_registry_rule()
    {
        Principal module = Principal.ForModule("software");

        foreach (RegistryEntry entry in CommandRegistry.Entries.Where(e => !OpenToModules.Contains(e.Name)))
        {
            DomainException refused = Assert.Throws<DomainException>(() => CommandRegistry.Require(entry.Name, module));
            Assert.Equal($"'{entry.Name}' is not open to the Module role.", refused.Message);
        }

        foreach (string name in OpenToModules)
        {
            Assert.Equal(name, CommandRegistry.Require(name, module).Name);
        }
    }

    [Fact]
    public void Every_other_role_is_open_to_what_it_was_and_only_the_owner_changes_a_projects_modules()
    {
        RegistryEntry setModules = Assert.Single(CommandRegistry.Entries, e => e.Name == "project.set-modules");
        Assert.Equal(EntryKind.Command, setModules.Kind);
        Assert.Equal(Owner, setModules.AllowedRoles);

        IEnumerable<RegistryEntry> others = CommandRegistry.Entries.Where(e => e != setModules);
        Assert.Equal(Before.Keys.Order(StringComparer.Ordinal), others.Select(e => e.Name).Order(StringComparer.Ordinal));
        foreach (RegistryEntry entry in others)
        {
            Assert.Equal(Before[entry.Name].Order(), entry.AllowedRoles.Where(r => r != Role.Module).Order());
        }
    }
}
