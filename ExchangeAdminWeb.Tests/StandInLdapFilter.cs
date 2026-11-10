using System.Text;
using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Reads an LDAP search filter the way a directory does, for the stand-in Active Directory cmdlets:
/// the two shapes the services build - one <c>(attribute=value)</c> clause, or several inside
/// <c>(|...)</c> - with the value's <c>\XX</c> escapes read as the one character each names and a
/// bare <c>*</c> as a wildcard. So a value the service did not escape widens the match or makes the
/// filter unreadable here, as it would in a directory. Public so a stand-in function in a
/// PowerShell session can call it.
/// </summary>
public static class StandInLdapFilter
{
    // A value holds no bare parenthesis, backslash or NUL: each of those must be written \XX.
    private static readonly Regex Clause = new(
        @"\G\((?<attribute>[A-Za-z][A-Za-z0-9-]*)=(?<value>(?:[^()\\\0]|\\[0-9A-Fa-f]{2})*)\)",
        RegexOptions.CultureInvariant);

    /// <summary>
    /// The filter's clauses, each an attribute name and an anchored pattern its value must match
    /// (compare case-insensitively); null when the filter is in no shape a directory would accept.
    /// </summary>
    public static string[][]? Read(string? filter)
    {
        if (string.IsNullOrEmpty(filter))
            return null;

        var body = filter.StartsWith("(|", StringComparison.Ordinal) && filter.EndsWith(')') ? filter[2..^1] : filter;
        var clauses = new List<string[]>();
        var at = 0;
        while (at < body.Length)
        {
            var clause = Clause.Match(body, at);
            if (!clause.Success)
                return null;
            clauses.Add([clause.Groups["attribute"].Value, Pattern(clause.Groups["value"].Value)]);
            at = clause.Index + clause.Length;
        }

        return clauses.Count == 0 ? null : clauses.ToArray();
    }

    private static string Pattern(string value)
    {
        // A value of one bare * asks whether the attribute is present at all.
        if (value == "*")
            return @"\A.+\z";

        var pattern = new StringBuilder(@"\A");
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '*')
            {
                pattern.Append(".*");
            }
            else if (value[i] == '\\')
            {
                pattern.Append(Regex.Escape(((char)Convert.ToInt32(value.Substring(i + 1, 2), 16)).ToString()));
                i += 2;
            }
            else
            {
                pattern.Append(Regex.Escape(value[i].ToString()));
            }
        }

        return pattern.Append(@"\z").ToString();
    }
}
