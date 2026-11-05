using System.Text.Json.Serialization;

namespace Coanda.Core.Definitions;

/// <summary>
/// Where a definition applies, nearest wins by name: a project's own, the owner's across every
/// project they hold, or the one a module brought, imported in module scope when the module was
/// installed. Stored by name; a record written before module scope took the place of shipped scope
/// names it <c>Shipped</c>, which reads as <see cref="Module"/>.
/// </summary>
[JsonConverter(typeof(DefinitionScopeConverter))]
public enum DefinitionScope
{
    Project,
    Owner,
    Module,
}
