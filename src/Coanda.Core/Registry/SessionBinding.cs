namespace Coanda.Core.Registry;

/// <summary>
/// What a judgment's session principal is bound to: the project and flow it was spawned for, and
/// the launch, the value the daemon assigned when it recorded that judgment's spawn. A visit can
/// launch more than once, so the launch, not the visit, is the identity.
/// </summary>
public sealed record SessionBinding(string Project, Guid FlowId, Guid LaunchId);
