namespace Coanda.Daemon.Api.Endpoints;

/// <summary>This node's id, its public key in the line format's key form, and the key's fingerprint, which <c>coa join</c> reads.</summary>
public sealed record NodeView(Guid NodeId, string PublicKey, string Fingerprint);

/// <summary>Vouches, in <see cref="Project"/>, for the node <see cref="NodeId"/> and its exact key <see cref="Key"/>.</summary>
public sealed record NodeVouchRequest(string Project, string NodeId, string Key);

/// <summary>Joins the project at <see cref="Remote"/> whose owner's root key has the fingerprint <see cref="Root"/>.</summary>
public sealed record JoinRequest(string Remote, string Root);
