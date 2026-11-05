using Coanda.Core.Identity.Events;

namespace Coanda.Core.Identity;

/// <summary>
/// An identity event as <see cref="IdentityTrust"/> weighs it: the event, the public key that signed
/// it (32 bytes in standard base64 with padding), the node id of the origin whose chain carries it,
/// and its sequence in that origin's chain for the project.
/// </summary>
public sealed record IdentityRecord(IIdentityEvent Event, string Signer, Guid Origin, long Sequence);
