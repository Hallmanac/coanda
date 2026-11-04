using Coanda.Core.Flows;

namespace Coanda.Daemon.Executors;

/// <summary>
/// A session the executor no longer holds, found on the retry its judgment resumed it for, with
/// the usage the collects of its attempts before that retry returned. Nothing recorded that usage
/// at the daemon stop, so the flow counts it when it pauses for the lost session; the retry's own
/// usage was not observed.
/// </summary>
public sealed record LostSession(SessionHandle Handle, Measurement Earlier) : Reattachment(Handle);
