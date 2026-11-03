namespace Coanda.Core.Types;

/// <summary>A flow a parcel type starts on a parcel when the parcel enters <see cref="State"/>.</summary>
public sealed record FlowBinding(string State, string Flow);
