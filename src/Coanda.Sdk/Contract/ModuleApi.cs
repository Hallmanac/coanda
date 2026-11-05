namespace Coanda.Sdk.Contract;

/// <summary>
/// The module API this SDK speaks. Its schemas are in <c>schemas/module/v1/</c> at the root of the
/// repository, and every contract type here is one document of them.
/// </summary>
public static class ModuleApi
{
    /// <summary>The module API version the manifest, the approval record, and describe name.</summary>
    public const int Version = 1;
}
