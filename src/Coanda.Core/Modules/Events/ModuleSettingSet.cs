namespace Coanda.Core.Modules.Events;

/// <summary>
/// One replicated setting of a module was given a value in a project, replacing the value it held. The
/// value is the JSON text the module's settings schema accepted for the name. It is recorded in the
/// project's own tenant, so it replicates with the project.
/// </summary>
public sealed record ModuleSettingSet(
    Guid SettingsId,
    string Project,
    string Module,
    string Name,
    string Value,
    string SetBy,
    DateTimeOffset SetAt);
