using Coanda.Cli;
using Coanda.Daemon.Executors;
using Coanda.Daemon.Modules;
using Coanda.Daemon.Projects;
using Coanda.Modules.Software;
using Coanda.Sdk.Contract;
using Fisher;

namespace Coanda.Tests.Daemon;

/// <summary>Project services over a store opened without the daemon, switching on the modules <c>coa</c> switches on.</summary>
internal static class ProjectServices
{
    /// <summary>The modules <c>coa daemon run</c> switches a new project on for (<see cref="ModuleComposition.DefaultModules"/>).</summary>
    public static readonly DefaultModules Defaults = new(ModuleComposition.DefaultModules);

    /// <summary>
    /// A project service whose daemon holds no credential profiles and whose only module settings schema
    /// is the software module's, the module every registration switches on.
    /// </summary>
    public static ProjectService Software(IDocumentStore store, TimeProvider clock) => Software(store, clock, Defaults);

    /// <summary>
    /// A project service whose daemon holds no credential profiles and whose only module settings schema
    /// is the software module's, switching a registration on for <paramref name="defaults"/>.
    /// </summary>
    public static ProjectService Software(IDocumentStore store, TimeProvider clock, DefaultModules defaults) =>
        new(
            store,
            clock,
            new CredentialProfiles([]),
            new ModuleSettingsSchemas(new Dictionary<string, SettingsSchema> { [SoftwareSteps.Module] = SoftwareDescribe.Settings }),
            defaults);
}
