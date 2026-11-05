using Coanda.Core;
using Coanda.Core.Flows;
using Coanda.Core.Registry;
using Coanda.Sdk.Contract;

namespace Coanda.Daemon.Modules;

/// <summary>
/// The settings schemas of a fixed set of modules, by module name, each checked when it is given: the
/// module's name is a plain name, and each replicated setting has a plain name, unique among them, that
/// is not <see cref="ModuleSettingsService.CredentialProfileName"/>, which names the credential profile
/// setting wherever settings are listed by name.
/// </summary>
public sealed class ModuleSettingsSchemas : IModuleSettingsSchemas
{
    private readonly Dictionary<string, SettingsSchema> schemas;

    public ModuleSettingsSchemas(IReadOnlyDictionary<string, SettingsSchema> schemas)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        this.schemas = new Dictionary<string, SettingsSchema>(StringComparer.Ordinal);
        foreach ((string module, SettingsSchema schema) in schemas)
        {
            this.schemas.Add(ModuleNames.Require(module), Check(module, schema));
        }
    }

    /// <summary>Holds no module's settings schema.</summary>
    public static ModuleSettingsSchemas None { get; } = new(new Dictionary<string, SettingsSchema>());

    public IReadOnlyCollection<string> Modules => schemas.Keys;

    public SettingsSchema? Find(string module) => schemas.GetValueOrDefault(module);

    private static SettingsSchema Check(string module, SettingsSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (ReplicatedSetting setting in schema.Replicated)
        {
            if (!Flow.IsPlainName(setting.Name) || string.Equals(setting.Name, ModuleSettingsService.CredentialProfileName, StringComparison.Ordinal))
            {
                throw new DomainException(
                    $"Module '{module}' declares a replicated setting named '{setting.Name}'; a setting's name is plain (letters, digits, hyphens, and underscores) and is not '{ModuleSettingsService.CredentialProfileName}'.");
            }

            if (!seen.Add(setting.Name))
            {
                throw new DomainException($"Module '{module}' declares two replicated settings named '{setting.Name}'; a module's setting names are unique.");
            }
        }

        return schema;
    }
}
