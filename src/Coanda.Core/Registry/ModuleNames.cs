using Coanda.Core.Flows;

namespace Coanda.Core.Registry;

/// <summary>
/// The names a module goes by and the names it prefixes. A module's name is lowercase letters and
/// digits in runs joined by single hyphens, the name pattern of the module manifest's schema
/// (<c>^[a-z0-9]+(-[a-z0-9]+)*$</c>). A system step a module brings and an external event kind it
/// records carry its name before a slash, <c>&lt;module&gt;/&lt;name&gt;</c>, with a plain name after it.
/// </summary>
public static class ModuleNames
{
    /// <summary>What a module's name is, as a refusal states it.</summary>
    public const string Rule = "lowercase letters and digits in runs joined by single hyphens";

    /// <summary>
    /// Whether <paramref name="name"/> may name a module: lowercase letters and digits in runs joined by
    /// single hyphens, starting and ending with a letter or digit, with nothing after the last one.
    /// </summary>
    public static bool IsModuleName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name[0] == '-' || name[^1] == '-')
        {
            return false;
        }

        for (int i = 0; i < name.Length; i++)
        {
            bool allowed = name[i] switch
            {
                >= 'a' and <= 'z' or >= '0' and <= '9' => true,
                '-' => name[i - 1] != '-',
                _ => false,
            };

            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Returns <paramref name="name"/> when it may name a module, and refuses it otherwise.</summary>
    public static string Require(string? name) =>
        IsModuleName(name)
            ? name ?? string.Empty
            : throw new DomainException($"A module's name is {Rule}; '{name}' is not.");

    /// <summary>
    /// The module <paramref name="name"/> is prefixed with when it has the form
    /// <c>&lt;module&gt;/&lt;name&gt;</c>, a module's name before the slash and a plain name after it,
    /// or null when it does not.
    /// </summary>
    public static string? PrefixOf(string? name)
    {
        if (name is null)
        {
            return null;
        }

        int slash = name.IndexOf('/', StringComparison.Ordinal);
        return slash >= 0 && IsModuleName(name[..slash]) && Flow.IsPlainName(name[(slash + 1)..])
            ? name[..slash]
            : null;
    }

    /// <summary>Whether <paramref name="name"/> has the form <c>&lt;module&gt;/&lt;name&gt;</c>.</summary>
    public static bool IsPrefixed(string? name) => PrefixOf(name) is not null;
}
