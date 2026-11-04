namespace Coanda.Core.Parcels;

/// <summary>A reference to something outside the parcel: an identifier, and a URL where one exists.</summary>
public sealed record FieldReference(string Id, string? Url = null);
