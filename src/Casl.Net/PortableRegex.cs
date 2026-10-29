using System.Text;
using System.Text.RegularExpressions;

namespace Casl;

/// <summary>
/// Makes a <c>$regex</c> pattern mean the same in .NET (in memory, and SQLite's <c>REGEXP</c>) and in PostgreSQL
/// (through Npgsql, which runs it with the <c>~</c> operator), or refuses it.
///
/// Refused, because PostgreSQL reads them differently or not at all: <c>\b \B \d \D \w \W \s \S \A \Z \z \G \p \P</c>,
/// <c>\x</c> and numeric escapes (back-references included), named, atomic and conditional groups, inline options and
/// comments, <c>[</c> inside a character class (POSIX classes, class subtraction), and the options <c>m</c> and <c>s</c>
/// together (Npgsql then matches newline-sensitively, so <c>.</c> stops matching newlines).
///
/// Rewritten, because Npgsql matches newline-sensitively unless only <c>s</c> is set, and without <c>m</c>
/// PostgreSQL's <c>$</c> is the very end of the text where .NET's also matches before a final newline:
/// - without <c>m</c>, <c>$</c> becomes <c>$(?![\s\S])</c>;
/// - unless only <c>s</c> is set, <c>[^...]</c> becomes <c>(?:(?!\n)[^...])</c>.
/// </summary>
internal static class PortableRegex
{
    private static readonly string[] Groups = ["(?:", "(?=", "(?!", "(?<=", "(?<!"];

    public static string Rewrite(string pattern, RegexOptions options, string path)
    {
        bool multiline = options.HasFlag(RegexOptions.Multiline);
        bool singleline = options.HasFlag(RegexOptions.Singleline);
        bool extended = options.HasFlag(RegexOptions.IgnorePatternWhitespace);
        if (multiline && singleline)
        {
            throw new CaslConfigurationException($"'$options' on '{path}' can't combine 'm' and 's': databases then stop matching newlines with '.'.");
        }

        StringBuilder result = new(pattern.Length + 16);
        bool inClass = false;
        bool wrappedClass = false;
        int classStart = 0;
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\\')
            {
                int length = EscapeLength(pattern, i)
                    ?? throw new CaslConfigurationException(
                        $"'$regex' on '{path}' uses '\\{pattern[i + 1]}', which databases read differently or not at all. Use a character class such as [0-9] or [A-Za-z] instead.");
                result.Append(pattern, i, length);
                i += length - 1;
                continue;
            }

            if (inClass)
            {
                if (c == '[')
                {
                    throw new CaslConfigurationException($"'$regex' on '{path}' uses '[' inside a character class; escape it as '\\['. POSIX classes and class subtraction aren't supported.");
                }

                result.Append(c);
                if (c == ']' && i > classStart)
                {
                    inClass = false;
                    if (wrappedClass)
                    {
                        result.Append(')');
                        wrappedClass = false;
                    }
                }

                continue;
            }

            if (extended && c == '#')
            {
                int end = pattern.IndexOf('\n', i);
                end = end < 0 ? pattern.Length : end + 1;
                result.Append(pattern, i, end - i); // a comment, to the end of the line
                i = end - 1;
                continue;
            }

            switch (c)
            {
                case '[':
                    bool negated = i + 1 < pattern.Length && pattern[i + 1] == '^';
                    if (negated && !singleline)
                    {
                        result.Append("(?:(?!\\n)");
                        wrappedClass = true;
                    }

                    result.Append(negated ? "[^" : "[");
                    i += negated ? 1 : 0;
                    inClass = true;
                    classStart = i + 1; // a ']' right after '[' or '[^' is a literal
                    break;
                case '$':
                    result.Append(multiline ? "$" : "$(?![\\s\\S])");
                    break;
                case '(' when i + 1 < pattern.Length && pattern[i + 1] == '?':
                    string group = Groups.FirstOrDefault(g => string.CompareOrdinal(pattern, i, g, 0, g.Length) == 0)
                        ?? throw new CaslConfigurationException(
                            $"'$regex' on '{path}' uses a group databases don't support; only (...), (?:...), (?=...), (?!...), (?<=...) and (?<!...) are allowed.");
                    result.Append(group);
                    i += group.Length - 1;
                    break;
                default:
                    result.Append(c);
                    break;
            }
        }

        return result.ToString();
    }

    /// <summary>The length of the escape at <paramref name="i"/> when both engines read it the same way, else null.</summary>
    private static int? EscapeLength(string pattern, int i)
    {
        if (i + 1 >= pattern.Length)
        {
            return null;
        }

        char next = pattern[i + 1];
        if (!char.IsLetterOrDigit(next))
        {
            return 2; // an escaped symbol: \. \[ \$ \\ ...
        }

        return next switch
        {
            't' or 'n' or 'r' or 'f' or 'v' => 2,
            'u' when i + 6 <= pattern.Length && pattern.Substring(i + 2, 4).All(char.IsAsciiHexDigit) => 6,
            _ => null,
        };
    }
}
