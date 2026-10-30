// The pattern translation is a port of CASL's fieldPatternMatcher
// (https://github.com/stalniy/casl, packages/casl-ability/src/matchers/field.ts),
// MIT License, Copyright (c) 2017-present Sergii Stotskyi.

using System.Text.RegularExpressions;

namespace Casl;

/// <summary>
/// Matches a field against a rule's fields, which may contain wildcards:
/// <c>author.*</c> (one level, and <c>author</c> itself), <c>author.**</c> (any depth),
/// <c>author.*.name</c>, <c>**.name</c>, <c>street*</c>.
/// </summary>
internal sealed partial class FieldMatcher
{
    private readonly IReadOnlyList<string> _fields;
    private readonly Regex? _pattern;

    public FieldMatcher(IReadOnlyList<string> fields)
    {
        _fields = fields;
        _pattern = fields.All(f => !f.Contains('*')) ? null : CreatePattern(fields);
    }

    public bool Matches(string field) => _pattern is null ? _fields.Contains(field) : _pattern.IsMatch(field);

    [GeneratedRegex(@"[-/\\^$+?.()|[\]{}]")]
    private static partial Regex SpecialChars();

    [GeneratedRegex(@"\.?\*+\.?")]
    private static partial Regex AnyStars();

    [GeneratedRegex(@"\*+")]
    private static partial Regex Stars();

    private static Regex CreatePattern(IReadOnlyList<string> fields)
    {
        IEnumerable<string> patterns = fields.Select(field =>
        {
            string escaped = SpecialChars().Replace(field, m =>
                m.Value == "." && ((m.Index > 0 && field[m.Index - 1] == '*') || (m.Index + 1 < field.Length && field[m.Index + 1] == '*'))
                    ? m.Value
                    : "\\" + m.Value);

            return AnyStars().Replace(escaped, m =>
            {
                string match = m.Value;
                string quantifier = escaped[0] == '*' || (match[0] == '.' && match[^1] == '.') ? "+" : "*";
                string matcher = match.Contains("**") ? "." : "[^.]";
                string pattern = Stars().Replace(match.Replace(".", "\\."), matcher + quantifier, 1);
                return m.Index + match.Length == escaped.Length ? $"(?:{pattern})?" : pattern;
            });
        });

        List<string> list = patterns.ToList();
        string combined = list.Count > 1 ? $"(?:{string.Join("|", list)})" : list[0];
        // No lookarounds or back-references, so the non-backtracking engine matches in linear time whatever the pattern.
        return new Regex($"^{combined}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    }
}
