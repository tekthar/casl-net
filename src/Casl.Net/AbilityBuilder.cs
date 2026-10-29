using System.Linq.Expressions;
using System.Text.Json.Nodes;

namespace Casl;

/// <summary>
/// Collects rules (from code, from a database, from role grants), fills in placeholders and validates them before
/// handing out an <see cref="Ability"/>. Invalid rules fail the build instead of being skipped, because skipping a
/// <c>cannot</c> rule would widen access.
/// </summary>
public sealed class AbilityBuilder
{
    private readonly SubjectRegistry _subjects;
    private readonly AbilityOptions _options;
    private readonly VariableScope _variables = new();
    private readonly List<Entry> _rules = [];

    /// <summary>A builder with an open registry and CASL semantics, like CASL's <c>AbilityBuilder(createMongoAbility)</c>.</summary>
    public AbilityBuilder()
        : this(SubjectRegistry.Open())
    {
    }

    /// <summary>A builder with an open registry and the given options (semantics, aliases, names).</summary>
    public AbilityBuilder(AbilityOptions options)
        : this(SubjectRegistry.Open(), options)
    {
    }

    /// <summary>
    /// A builder whose rules are checked against <paramref name="subjects"/>. With a strict registry, conditions may
    /// only use the member paths it lists and field-level rules only its fields.
    /// </summary>
    public AbilityBuilder(SubjectRegistry subjects, AbilityOptions? options = null)
    {
        _subjects = subjects;
        _options = options ?? AbilityOptions.Default;
    }

    /// <summary>The rules added so far, in order, before placeholders are filled in.</summary>
    public IReadOnlyList<RawRule> Rules => _rules.Select(e => e.Rule).ToList();

    /// <summary>Variables every rule can use, e.g. <c>new { user = new { id, groupIds } }</c>.</summary>
    public AbilityBuilder WithVariables(object variables)
    {
        _variables.Push(variables);
        return this;
    }

    /// <summary>Adds a rule. <paramref name="variables"/> apply to this rule only, e.g. a grant's scope.</summary>
    public AbilityBuilder Add(RawRule rule, object? variables = null)
    {
        _rules.Add(new Entry(rule, variables));
        return this;
    }

    /// <summary>Adds rules in order, for example from a database. <paramref name="variables"/> apply to these rules only.</summary>
    public AbilityBuilder AddRange(IEnumerable<RawRule> rules, object? variables = null)
    {
        foreach (RawRule rule in rules)
        {
            Add(rule, variables);
        }

        return this;
    }

    /// <summary>Allows <paramref name="action"/> on <paramref name="subject"/> (a claim rule when null).</summary>
    public RuleBuilder Can(string action, string? subject = null, object? conditions = null, params string[] fields) =>
        AddRule([action], subject is null ? null : [subject], conditions, fields, inverted: false, reason: null);

    /// <summary>Allows several actions on several subjects in one rule.</summary>
    public RuleBuilder Can(IReadOnlyList<string> actions, IReadOnlyList<string>? subjects, object? conditions = null, params string[] fields) =>
        AddRule(actions, subjects, conditions, fields, inverted: false, reason: null);

    /// <summary>Forbids <paramref name="action"/> on <paramref name="subject"/>; <paramref name="reason"/> becomes the error message.</summary>
    public RuleBuilder Cannot(string action, string? subject = null, object? conditions = null, string? reason = null, params string[] fields) =>
        AddRule([action], subject is null ? null : [subject], conditions, fields, inverted: true, reason);

    /// <summary>Forbids several actions on several subjects in one rule.</summary>
    public RuleBuilder Cannot(IReadOnlyList<string> actions, IReadOnlyList<string>? subjects, object? conditions = null, string? reason = null, params string[] fields) =>
        AddRule(actions, subjects, conditions, fields, inverted: true, reason);

    /// <summary>Allows <paramref name="action"/> on the subject registered for <typeparamref name="TSubject"/> (its class name in an open registry).</summary>
    public RuleBuilder Can<TSubject>(string action, object? conditions = null, params string[] fields) =>
        Can(action, _subjects.Get(typeof(TSubject)).Name, conditions, fields);

    /// <summary>Forbids <paramref name="action"/> on the subject registered for <typeparamref name="TSubject"/>.</summary>
    public RuleBuilder Cannot<TSubject>(string action, object? conditions = null, string? reason = null, params string[] fields) =>
        Cannot(action, _subjects.Get(typeof(TSubject)).Name, conditions, reason, fields);

    private RuleBuilder AddRule(IReadOnlyList<string> actions, IReadOnlyList<string>? subjects, object? conditions, string[] fields, bool inverted, string? reason)
    {
        RawRule rule = new()
        {
            Action = actions,
            Subject = subjects,
            Conditions = RawRule.ToConditions(conditions),
            Fields = fields.Length == 0 ? null : fields,
            Inverted = inverted,
            Reason = reason,
        };
        _rules.Add(new Entry(rule, null));
        return new RuleBuilder(this, _rules.Count - 1);
    }

    internal void Update(int index, Func<RawRule, RawRule> change) =>
        _rules[index] = _rules[index] with { Rule = change(_rules[index].Rule) };

    /// <summary>
    /// Fills in placeholders, validates every rule against the registry and returns the immutable
    /// <see cref="Ability"/>. Throws <see cref="CaslConfigurationException"/> for a rule that can't be enforced
    /// exactly (unknown subject, path, operator or placeholder, or a value of the wrong type).
    /// </summary>
    public Ability Build()
    {
        ActionAliases? aliases = _options.Aliases;
        List<Rule> rules = new(_rules.Count);
        for (int i = 0; i < _rules.Count; i++)
        {
            (RawRule rule, object? ruleVariables) = _rules[i];
            VariableScope scope = _variables.With(ruleVariables);
            JsonObject? conditions = rule.Conditions is null ? null : Placeholders.Interpolate(rule.Conditions, scope.Resolve);
            RawRule interpolated = rule with { Conditions = conditions };
            Condition? condition = Check(interpolated, _subjects, _options, validateValues: true);
            IReadOnlyList<string> actions = aliases is null ? interpolated.Action : aliases.Expand(interpolated.Action);
            rules.Add(new Rule(interpolated, actions, interpolated.Subject is { Count: > 0 } s ? s : [_options.AnySubjectType], condition, priority: i));
        }

        return new Ability(_subjects, _options, rules);
    }

    /// <summary>
    /// Checks a rule before it is saved, e.g. from an admin screen. Placeholders are allowed and not filled in.
    /// Returns the problems found; an empty list means the rule is valid.
    /// </summary>
    public static IReadOnlyList<string> Validate(RawRule rule, SubjectRegistry subjects, AbilityOptions? options = null)
    {
        try
        {
            _ = Check(rule, subjects, options ?? AbilityOptions.Default, validateValues: false);
            return [];
        }
        catch (CaslConfigurationException ex)
        {
            return [ex.Message];
        }
    }

    /// <summary>Parses the rule's conditions and checks it against the registry; returns the parsed condition.</summary>
    internal static Condition? Check(RawRule rule, SubjectRegistry subjects, AbilityOptions options, bool validateValues)
    {
        if (rule.Action.Count == 0 || rule.Action.Any(string.IsNullOrWhiteSpace))
        {
            throw new CaslConfigurationException("A rule needs at least one action.");
        }

        if (rule.Fields is { Count: 0 })
        {
            throw new CaslConfigurationException("The `rawRule.fields` array cannot be empty.");
        }

        if (rule.Subject is { Count: 0 })
        {
            // Read as "no subject", it would become a rule on every subject.
            throw new CaslConfigurationException("A rule's subject list can't be empty; leave the subject out for a claim rule.");
        }

        IReadOnlyList<string> subjectNames = rule.Subject is { Count: > 0 } s ? s : [options.AnySubjectType];
        foreach (string subject in subjectNames)
        {
            if (subject != options.AnySubjectType && !subjects.IsKnown(subject))
            {
                throw new CaslConfigurationException($"Unknown subject '{subject}'.");
            }
        }

        Condition? condition = rule.Conditions is { Count: > 0 } c ? ConditionParser.Parse(c) : null;
        foreach (string subjectName in subjectNames)
        {
            if (subjectName == options.AnySubjectType)
            {
                if (!subjects.IsOpen && (condition is not null || rule.Fields is not null))
                {
                    // A strict registry can't check conditions or fields without knowing the subject's type.
                    throw new CaslConfigurationException($"Rules on '{options.AnySubjectType}' can't have conditions or fields; name the subjects instead.");
                }

                continue;
            }

            if (!subjects.TryGet(subjectName, out SubjectDefinition? definition))
            {
                continue; // open registry, unregistered name: checked when an object is matched
            }

            if (!definition!.AllowAnyPath || definition.Fields.Count > 0)
            {
                foreach (string field in rule.Fields ?? [])
                {
                    if (!field.Contains('*') && !definition.Fields.Contains(field)
                        && !definition.Fields.Any(f => f.EndsWith(".**", StringComparison.Ordinal) && field.StartsWith(f[..^2], StringComparison.Ordinal)))
                    {
                        throw new CaslConfigurationException($"'{subjectName}' has no field '{field}' for field-level rules.");
                    }
                }
            }

            if (condition is not null)
            {
                // Building the expression checks every path, operator and value type.
                Condition toCheck = validateValues ? condition : WithoutPlaceholderValues(condition);
                _ = new ExpressionBuilder(definition, nullSafe: false).Build(toCheck, Expression.Parameter(definition.Type, "x"));
            }
        }

        return condition;
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

    private sealed record Entry(RawRule Rule, object? Variables);
}

/// <summary>Returned by <see cref="AbilityBuilder.Can(string, string?, object?, string[])"/> to refine the rule just added.</summary>
public sealed class RuleBuilder
{
    private readonly AbilityBuilder _builder;
    private readonly int _index;

    internal RuleBuilder(AbilityBuilder builder, int index)
    {
        _builder = builder;
        _index = index;
    }

    /// <summary>The rule as it stands.</summary>
    public RawRule Rule => _builder.Rules[_index];

    /// <summary>Limits the rule to instances matching a MongoDB-style query.</summary>
    public RuleBuilder Where(object conditions)
    {
        _builder.Update(_index, r => r with { Conditions = RawRule.ToConditions(conditions) });
        return this;
    }

    /// <summary>Limits the rule to fields (wildcards allowed: <c>author.*</c>, <c>**.name</c>).</summary>
    public RuleBuilder OnFields(params string[] fields)
    {
        _builder.Update(_index, r => r with { Fields = fields });
        return this;
    }

    /// <summary>The message shown when this rule denies access.</summary>
    public RuleBuilder Because(string reason)
    {
        _builder.Update(_index, r => r with { Reason = reason });
        return this;
    }

    /// <summary>Adds another allow rule. See <see cref="AbilityBuilder.Can(string, string?, object?, string[])"/>.</summary>
    public RuleBuilder Can(string action, string? subject = null, object? conditions = null, params string[] fields) =>
        _builder.Can(action, subject, conditions, fields);

    /// <summary>Adds another <c>cannot</c> rule. See <see cref="AbilityBuilder.Cannot(string, string?, object?, string?, string[])"/>.</summary>
    public RuleBuilder Cannot(string action, string? subject = null, object? conditions = null, string? reason = null, params string[] fields) =>
        _builder.Cannot(action, subject, conditions, reason, fields);

    /// <summary>Adds another allow rule on the subject registered for <typeparamref name="TSubject"/>.</summary>
    public RuleBuilder Can<TSubject>(string action, object? conditions = null, params string[] fields) =>
        _builder.Can<TSubject>(action, conditions, fields);

    /// <summary>Adds another <c>cannot</c> rule on the subject registered for <typeparamref name="TSubject"/>.</summary>
    public RuleBuilder Cannot<TSubject>(string action, object? conditions = null, string? reason = null, params string[] fields) =>
        _builder.Cannot<TSubject>(action, conditions, reason, fields);

    /// <summary>Adds a raw rule. See <see cref="AbilityBuilder.Add"/>.</summary>
    public AbilityBuilder Add(RawRule rule, object? variables = null) => _builder.Add(rule, variables);

    /// <summary>Adds raw rules in order. See <see cref="AbilityBuilder.AddRange"/>.</summary>
    public AbilityBuilder AddRange(IEnumerable<RawRule> rules, object? variables = null) => _builder.AddRange(rules, variables);

    /// <summary>Builds the ability. See <see cref="AbilityBuilder.Build"/>.</summary>
    public Ability Build() => _builder.Build();

    /// <summary>Lets a <see cref="RuleBuilder"/> stand in for its builder: <c>AbilityBuilder b = builder.Can(...)</c>.</summary>
    public static implicit operator AbilityBuilder(RuleBuilder rule) => rule._builder;
}
