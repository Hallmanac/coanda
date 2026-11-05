namespace Coanda.Sdk.Contract;

/// <summary>
/// The signed approval that lets a module start (<c>approval.schema.json</c>).
/// <paramref name="ArtifactHash"/> is <c>sha256:</c> and the lowercase hex digest;
/// <paramref name="Signer"/> and <paramref name="Signature"/> are an ed25519 public key and
/// signature in standard base64, the signature over the record without it in the JSON
/// Canonicalization Scheme. An approval the owner signed has no <paramref name="ReviewId"/>.
/// </summary>
/// <remarks>
/// <paramref name="IssuedAt"/> is an RFC 3339 date-time held as the exact text the signer wrote,
/// not as a <see cref="DateTimeOffset"/>, because the signature covers those characters: a parsed
/// date writes back as different text (<c>Z</c> becomes <c>+00:00</c>) and the signature would no
/// longer verify. Canonicalize the record's values for verification with a JSON Canonicalization
/// Scheme writer; <see cref="ModuleJson.Options"/> escapes characters such as <c>+</c>, so its
/// output is not the canonical form.
/// </remarks>
public sealed record ApprovalRecord(
    string Module,
    string Version,
    string ArtifactHash,
    int ModuleApiVersion,
    string SourceUrl,
    string SourceCommit,
    string Signer,
    string IssuedAt,
    string Signature,
    string? ReviewId = null);
