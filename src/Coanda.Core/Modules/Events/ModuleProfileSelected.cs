namespace Coanda.Core.Modules.Events;

/// <summary>
/// A credential profile this node holds for a module was selected for a project, replacing any selected
/// before. Only the profile's name is recorded, in the node tenant and never in the project's own, so
/// nothing the project replicates carries it, and nothing of the profile's contents is ever read.
/// </summary>
public sealed record ModuleProfileSelected(
    Guid SelectionId,
    string Project,
    string Module,
    string Profile,
    string SelectedBy,
    DateTimeOffset SelectedAt);
