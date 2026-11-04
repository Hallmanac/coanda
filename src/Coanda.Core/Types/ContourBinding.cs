namespace Coanda.Core.Types;

/// <summary>A contour a parcel type starts on a parcel when the parcel enters <see cref="State"/>.</summary>
public sealed record ContourBinding(string State, string Contour);
