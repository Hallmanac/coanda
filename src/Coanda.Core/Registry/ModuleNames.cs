using Coanda.Core.Flows;

namespace Coanda.Core.Registry;

/// <summary>
/// The names a module goes by and the names it prefixes. A module's name is a plain name (letters,
/// digits, hyphens, and underscores). A system step a module brings and an external event kind it
/// records carry its name before a slash, <c>&lt;module&gt;/&lt;name&gt;</c>, with a plain name after it.
/// </summary>
public static class ModuleNames
{
    /// <summary>Whether <paramref name="name"/> may name a module: a plain name.</summary>
    public static bool IsModuleName(string? name) => Flow.IsPlainName(name);

    /// <summary>Returns <paramref name="name"/> when it may name a module, and refuses it otherwise.</summary>
    public static string Require(string? name) =>
        IsModuleName(name)
            ? name ?? string.Empty
            : throw new DomainException(
                $"A module's name is a plain name (letters, digits, hyphens, and underscores); '{name}' is not.");

    /// <summary>
    /// The module <paramref name="name"/> is prefixed with when it has the form
    /// <c>&lt;module&gt;/&lt;name&gt;</c>, both parts plain names, or null when it does not.
    /// </summary>
    public static string? PrefixOf(string? name)
    {
        if (name is null)
        {
            return null;
        }

        int slash = name.IndexOf('/', StringComparison.Ordinal);
        return slash >= 0 && Flow.IsPlainName(name[..slash]) && Flow.IsPlainName(name[(slash + 1)..])
            ? name[..slash]
            : null;
    }

    /// <summary>Whether <paramref name="name"/> has the form <c>&lt;module&gt;/&lt;name&gt;</c>.</summary>
    public static bool IsPrefixed(string? name) => PrefixOf(name) is not null;
}
