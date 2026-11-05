namespace Coanda.Core.Identity.Events;

/// <summary>
/// One of the identity events: the facts about owner root, nodes, members, and succession that each
/// project's tenant records and that travel with the project. The trust rule in the core reads them.
/// </summary>
public interface IIdentityEvent;
