using System.Text.Json;

namespace Coanda.Sdk.Contract;

/// <summary>
/// One replicated setting a module declares: its plain name, the JSON Schema (draft-07) its value
/// must meet, whether the module requires it, and the value it takes when the project sets none, if
/// it has one. A default that is null is no default.
/// </summary>
public sealed record ReplicatedSetting(string Name, JsonElement Schema, bool Required, JsonElement? Default = null);
