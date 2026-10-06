using System.Text.Json.Nodes;

namespace Casl;

/// <summary>
/// A rule inside an <see cref="Ability"/>: the raw rule with placeholders filled in, its actions after aliases,
/// and its priority. The last rule added has the highest priority.
/// </summary>
public sealed class Rule
{
    private readonly FieldMatcher? _fieldMatcher;

    internal Rule(RawRule origin, IReadOnlyList<string> actions, IReadOnlyList<string> subjects, Condition? condition, int priority)
    {
        Origin = origin;
        Action = actions;
        Subject = subjects;
        Condition = condition;
        Priority = priority;
        _fieldMatcher = origin.Fields is { Count: > 0 } fields ? new FieldMatcher(fields) : null;
    }

    /// <summary>The rule as defined, with placeholders filled in.</summary>
    public RawRule Origin { get; }

    /// <summary>The rule's actions, including the ones its aliases expand to.</summary>
    public IReadOnlyList<string> Action { get; }

    /// <summary>The rule's subject types; a rule without a subject applies to the any-subject type (<c>all</c>).</summary>
    public IReadOnlyList<string> Subject { get; }

    /// <summary>The MongoDB-style conditions, or null when the rule applies to every instance (empty conditions count as none).</summary>
    public JsonObject? Conditions => Condition is null ? null : Origin.Conditions;

    public IReadOnlyList<string>? Fields => Origin.Fields;

    public bool Inverted => Origin.Inverted;

    public string? Reason => Origin.Reason;

    /// <summary>Higher wins: the last rule added has the highest priority.</summary>
    public int Priority { get; }

    internal Condition? Condition { get; }

    /// <summary>
    /// CASL's field check. Without a field (asking about the subject as a whole), a <c>cannot</c> rule limited to
    /// fields doesn't apply, and an allow rule limited to fields does.
    /// </summary>
    public bool MatchesField(string? field)
    {
        if (_fieldMatcher is null)
        {
            return true;
        }

        return field is null ? !Inverted : _fieldMatcher.Matches(field);
    }

    public override string ToString() => Origin.ToJson();
}
