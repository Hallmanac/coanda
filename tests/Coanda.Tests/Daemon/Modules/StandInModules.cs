using System.Text.Json;
using Coanda.Daemon.Modules;
using Coanda.Sdk.Contract;

namespace Coanda.Tests.Daemon.Modules;

/// <summary>
/// Stand-in modules with settings schemas of their own, filling the daemon's settings schema seam.
/// <see cref="ModuleNode.Module"/> requires a channel and a profile and defaults neither; <see cref="Relaxed"/>
/// requires a profile that defaults to the node's default identity and defaults its one required
/// setting; <see cref="ModuleNode.Other"/> declares one setting and no profile.
/// </summary>
internal static class StandInModules
{
    public const string Relaxed = "relaxed";

    /// <summary>A module that is switched on in a test without the node holding a settings schema for it.</summary>
    public const string Unknown = "unknown";

    public static SettingsSchema Strict { get; } = new(
        [
            new ReplicatedSetting("channel", Json("""{ "type": "string", "minLength": 1 }"""), Required: true),
            new ReplicatedSetting("limit", Json("""{ "type": "integer", "minimum": 1 }"""), Required: false, Default: Json("3")),
            new ReplicatedSetting("labels", Json("""{ "type": "array", "items": { "type": "string" } }"""), Required: false),
        ],
        new CredentialProfileSetting(Required: true, DefaultsToNodeIdentity: false));

    public static SettingsSchema RelaxedSchema { get; } = new(
        [new ReplicatedSetting("channel", Json("""{ "type": "string" }"""), Required: true, Default: Json("\"general\""))],
        new CredentialProfileSetting(Required: true, DefaultsToNodeIdentity: true));

    public static SettingsSchema OtherSchema { get; } = new(
        [new ReplicatedSetting("mapping", Json("""{ "type": "object" }"""), Required: false)]);

    public static IModuleSettingsSchemas Schemas { get; } = new ModuleSettingsSchemas(new Dictionary<string, SettingsSchema>
    {
        [ModuleNode.Module] = Strict,
        [Relaxed] = RelaxedSchema,
        [ModuleNode.Other] = OtherSchema,
    });

    public static JsonElement Json(string text)
    {
        using JsonDocument document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }
}
