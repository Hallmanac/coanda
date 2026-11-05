namespace Coanda.Sdk.Contract;

/// <summary>
/// The signed approval that lets a module start (<c>approval.schema.json</c>).
/// <paramref name="ArtifactHash"/> is <c>sha256:</c> and the lowercase hex digest;
/// <paramref name="Signer"/> and <paramref name="Signature"/> are an ed25519 public key and
/// signature in standard base64, the signature over the record without it in the JSON
/// Canonicalization Scheme. An approval the owner signed has no <paramref name="ReviewId"/>.
/// </summary>
public sealed record ApprovalRecord(
    string Module,
    string Version,
    string ArtifactHash,
    int ModuleApiVersion,
    string SourceUrl,
    string SourceCommit,
    string Signer,
    DateTimeOffset IssuedAt,
    string Signature,
    string? ReviewId = null);
