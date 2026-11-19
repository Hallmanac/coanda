using Coanda.Sdk.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Coanda.Daemon.Modules;

/// <summary>
/// A module the host that builds the executable composes and hands to the daemon, which builds it at start
/// from what the daemon provides (<see cref="ModuleHostContext"/>) and hosts it in its own process behind a
/// host adapter. The daemon makes its module area under the node home, issues it a core token for the start
/// once it listens, and imports the definitions in the directory at <see cref="Path"/> in module scope,
/// refusing a manifest there that names another module. The daemon never names the module's type: the
/// host does, through <see cref="Of"/>, and the built module is the one singleton of that type in the
/// daemon's services, so two composed modules of one type are refused at start.
/// </summary>
public sealed class ComposedModule
{
    private readonly Action<IServiceCollection, Func<IServiceProvider, ModuleHostContext>> register;
    private readonly Func<IServiceProvider, IModule> resolve;

    private ComposedModule(
        string name,
        string path,
        Action<IServiceCollection, Func<IServiceProvider, ModuleHostContext>> register,
        Func<IServiceProvider, IModule> resolve)
    {
        Name = name;
        Path = path;
        this.register = register;
        this.resolve = resolve;
    }

    /// <summary>The name the module is hosted as: the prefix of its steps, and the name a project switches it on by.</summary>
    public string Name { get; }

    /// <summary>The directory holding the module's <c>module.json</c> and its definitions folder.</summary>
    public string Path { get; }

    /// <summary>
    /// A module hosted as <paramref name="name"/>, whose manifest and definitions are in <paramref name="path"/>,
    /// made once at start by <paramref name="create"/> from the context the daemon gives it.
    /// </summary>
    public static ComposedModule Of<TModule>(string name, string path, Func<ModuleHostContext, TModule> create)
        where TModule : class, IModule
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(create);
        return new ComposedModule(
            name,
            path,
            (services, context) =>
            {
                if (services.Any(service => service.ServiceType == typeof(TModule)))
                {
                    throw new InvalidOperationException(
                        $"The module '{name}' is composed as a {typeof(TModule).Name}, and another composed module already is; the daemon hosts one module of each type.");
                }

                services.AddSingleton(provider => create(context(provider)));
            },
            provider => provider.GetRequiredService<TModule>());
    }

    /// <summary>Adds the module to the daemon's services, built from the context <paramref name="context"/> gives when it is first asked for.</summary>
    internal void Register(IServiceCollection services, Func<IServiceProvider, ModuleHostContext> context) => register(services, context);

    /// <summary>The module the daemon's services built.</summary>
    internal IModule Resolve(IServiceProvider services) => resolve(services);
}
