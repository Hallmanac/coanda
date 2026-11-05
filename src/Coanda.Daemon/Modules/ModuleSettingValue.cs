using System.Text.Json;

namespace Coanda.Daemon.Modules;

/// <summary>A value given to one of a module's replicated settings: the module, the setting's name, and the value as JSON.</summary>
public sealed record ModuleSettingValue(string Module, string Name, JsonElement Value);
