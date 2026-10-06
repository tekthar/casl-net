using System.Linq.Expressions;
using System.Text.Json.Nodes;

namespace Msar.Authz;

/// <summary>
/// Collects rules (from code, from a database, from role grants), fills in placeholders and validates
/// every rule against the subject registry before handing out an <see cref="Ability"/>.
/// Invalid rules fail the build instead of being skipped, because skipping a deny rule would widen access.
/// </summary>
public sealed class AbilityBuilder(SubjectRegistry subjects)
{
    private readonly VariableScope _variables = new();
    private readonly List<(RawRule Rule, object? Variables)> _rules = [];

    /// <summary>Variables every rule can use, e.g. <c>new { user = new { id, groupIds } }</c>.</summary>
    public AbilityBuilder WithVariables(object variables)
    {
        _variables.Push(variables);
        return this;
    }

    /// <summary>Adds a rule. <paramref name="variables"/> apply to this rule only, e.g. a grant's scope.</summary>
    public AbilityBuilder Add(RawRule rule, object? variables = null)
    {
        _rules.Add((rule, variables));
        return this;
    }

    public AbilityBuilder AddRange(IEnumerable<RawRule> rules, object? variables = null)
    {
        foreach (RawRule rule in rules)
        {
            Add(rule, variables);
        }

        return this;
    }

    public AbilityBuilder Can(string action, string subject, object? conditions = null, params string[] fields) =>
        Add(RawRule.Can(action, subject, conditions, fields.Length == 0 ? null : fields));

    public AbilityBuilder Cannot(string action, string subject, object? conditions = null, string? reason = null, params string[] fields) =>
        Add(RawRule.Cannot(action, subject, conditions, fields.Length == 0 ? null : fields, reason));

    public Ability Build()
    {
        List<CompiledRule> compiled = [];
        foreach ((RawRule rule, object? ruleVariables) in _rules)
        {
            VariableScope scope = _variables.With(ruleVariables);
            JsonObject? conditions = rule.Conditions is null ? null : Placeholders.Interpolate(rule.Conditions, scope.Resolve);
            RawRule interpolated = rule with { Conditions = conditions };
            compiled.Add(Compile(interpolated, subjects, validateValues: true));
        }

        return new Ability(subjects, compiled);
    }

    /// <summary>
    /// Checks a rule before it is saved, e.g. from an admin screen. Placeholders are allowed and not filled in.
    /// Returns the problems found; an empty list means the rule is valid.
    /// </summary>
    public static IReadOnlyList<string> Validate(RawRule rule, SubjectRegistry subjects)
    {
        try
        {
            _ = Compile(rule, subjects, validateValues: false);
            return [];
        }
        catch (AuthzConfigurationException ex)
        {
            return [ex.Message];
        }
    }

    internal static CompiledRule Compile(RawRule rule, SubjectRegistry subjects, bool validateValues)
    {
        if (rule.Action.Count == 0 || rule.Action.Any(string.IsNullOrWhiteSpace))
        {
            throw new AuthzConfigurationException("A rule needs at least one action.");
        }

        foreach (string subject in rule.Subject)
        {
            if (!subjects.IsKnown(subject))
            {
                throw new AuthzConfigurationException($"Unknown subject '{subject}'.");
            }
        }

        Condition? condition = rule.Conditions is { Count: > 0 } c ? ConditionParser.Parse(c) : null;
        foreach (string subjectName in rule.Subject)
        {
            if (subjectName == SubjectRegistry.AllSubjects)
            {
                if (condition is not null || rule.Fields is not null)
                {
                    throw new AuthzConfigurationException("Rules on 'all' can't have conditions or fields; name the subjects instead.");
                }

                continue;
            }

            SubjectDefinition definition = subjects.Get(subjectName);
            foreach (string field in rule.Fields ?? [])
            {
                if (!definition.Fields.Contains(field))
                {
                    throw new AuthzConfigurationException($"'{subjectName}' has no field '{field}' for field-level rules.");
                }
            }

            if (condition is not null)
            {
                // Building the expression checks every path, operator and value type.
                Condition toCheck = validateValues ? condition : WithoutPlaceholderValues(condition);
                _ = new ExpressionBuilder(definition, nullSafe: false).Build(toCheck, Expression.Parameter(definition.Type, "x"));
            }
        }

        return new CompiledRule(rule, [.. rule.Action], [.. rule.Subject], condition);
    }

    // Placeholders can't be type-checked before they're filled in; check paths and operators only.
    private static Condition WithoutPlaceholderValues(Condition condition) => condition switch
    {
        AndCondition a => new AndCondition(a.Items.Select(WithoutPlaceholderValues).ToList()),
        OrCondition o => new OrCondition(o.Items.Select(WithoutPlaceholderValues).ToList()),
        NotCondition n => new NotCondition(WithoutPlaceholderValues(n.Inner)),
        ElemMatchCondition e => new ElemMatchCondition(e.Path, WithoutPlaceholderValues(e.Inner)),
        FieldCondition f when Placeholders.IsWholePlaceholder(f.Value) || (f.Value is JsonArray arr && arr.Any(Placeholders.IsWholePlaceholder)) =>
            new FieldCondition(f.Path, "$exists", JsonValue.Create(true)),
        _ => condition,
    };
}
