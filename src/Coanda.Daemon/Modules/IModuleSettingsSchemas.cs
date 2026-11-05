using Coanda.Sdk.Contract;

namespace Coanda.Daemon.Modules;

/// <summary>
/// The one seam through which the daemon reads a module's settings schema, by the module's name. No
/// module is hosted yet: the in-process module will fill it from its describe answer, and a test fills
/// it with stand-in modules (<see cref="ModuleSettingsSchemas"/>). A module it holds no schema for has
/// no settings this node can set, select, or show.
/// </summary>
public interface IModuleSettingsSchemas
{
    /// <summary>The name of every module the seam holds a settings schema for.</summary>
    IReadOnlyCollection<string> Modules { get; }

    /// <summary>The settings schema of the module <paramref name="module"/>, or null when the seam holds none for it.</summary>
    SettingsSchema? Find(string module);
}
