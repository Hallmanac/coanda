using System.Text;
using System.Text.RegularExpressions;

namespace Coanda.Tests;

/// <summary>
/// Turns a message pattern in which <c>*</c> matches any run of characters and <c>?</c> matches one
/// character into an anchored regular expression for <see cref="Assert.Matches(string, string?)"/>.
/// Every other character matches only itself.
/// </summary>
public static class WildcardPattern
{
    public static string ToRegex(string pattern)
    {
        var regex = new StringBuilder("(?s)^");
        foreach (char c in pattern)
        {
            regex.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(c.ToString()),
            });
        }

        return regex.Append('$').ToString();
    }
}
