// RulesToCondition, PermittedFields and DefaultValues are ports of CASL's rulesToCondition, permittedFieldsOf and
// rulesToFields (https://github.com/stalniy/casl, packages/casl-ability/src/extra),
// MIT License, Copyright (c) 2017-present Sergii Stotskyi. See THIRD-PARTY-NOTICES.md.

using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Text.Json.Nodes;

namespace Casl;

/// <summary>
/// What one user may do: a set of rules with placeholders filled in.
///
/// By default rules combine as in CASL (<see cref="RuleSemantics.Casl"/>): the last rule that matches wins.
/// With <see cref="RuleSemantics.DenyOverrides"/>, any matching <c>cannot</c> rule wins regardless of order.
/// Either way the same decision is made in memory (<see cref="Can(string, object?, string?)"/>), in SQL (<see cref="Filter{T}"/>) and,
/// through <see cref="ToCaslRules"/>, in the browser by @casl/ability.
/// </summary>
public sealed class Ability
{
    private readonly SubjectRegistry _subjects;
    private readonly AbilityOptions _options;
    private readonly IReadOnlyList<Rule> _rules;       // as defined
    private readonly IReadOnlyList<Rule> _byPriority;  // last defined first
    private readonly bool _hasFieldRules;
    private readonly Dictionary<string, List<string>> _actionsBySubject;
    private readonly ConcurrentDictionary<(string Action, Type Type), LambdaExpression> _filters = new();
    private readonly ConcurrentDictionary<(Rule Rule, string Subject, Type Type), Func<object, bool>> _checks = new();

    internal Ability(SubjectRegistry subjects, AbilityOptions options, IReadOnlyList<Rule> rules)
    {
        _subjects = subjects;
        _options = options;
        _rules = rules;
        _byPriority = rules.OrderByDescending(r => r.Priority).ToList();
        _hasFieldRules = rules.Any(r => r.Fields is not null);
        _actionsBySubject = IndexActions(_byPriority);
    }

    public AbilityOptions Options => _options;

    /// <summary>The rules with placeholders filled in, in the order they were added.</summary>
    public IReadOnlyList<RawRule> Rules => _rules.Select(r => r.Origin).ToList();

    // ---- checks ----

    /// <summary>
    /// Whether the action is allowed. <paramref name="subject"/> is a subject type name (<c>"Post"</c>), a CLR type,
    /// an object, an object tagged with <see cref="Subject.Of"/>, or null for claim rules (subject <c>all</c>).
    /// A type-level check answers "on at least some instances".
    /// </summary>
    public bool Can(string action, object? subject = null, string? field = null) =>
        RelevantRuleFor(action, subject, field) is { Inverted: false };

    /// <summary>
    /// Like <see cref="Can(string, object?, string?)"/>, but with <see cref="FieldMatch.Named"/> only allow rules that list
    /// fields (and match <paramref name="field"/>) grant access: rules without a field list don't reach the field. Denies
    /// count either way. For fields that must be granted by name, such as a salary or a cost.
    /// </summary>
    public bool Can(string action, object? subject, string field, FieldMatch match) =>
        RelevantRuleFor(action, subject, field, match) is { Inverted: false };

    public bool Cannot(string action, object? subject = null, string? field = null) => !Can(action, subject, field);

    /// <summary>The rule that decides the check, or null when no rule matches (which means "no").</summary>
    public Rule? RelevantRuleFor(string action, object? subject = null, string? field = null) =>
        RelevantRuleFor(action, subject, field, FieldMatch.Any);

    /// <summary>The rule that decides the check; with <see cref="FieldMatch.Named"/>, allow rules without fields are skipped.</summary>
    public Rule? RelevantRuleFor(string action, object? subject, string? field, FieldMatch match)
    {
        string subjectType = DetectSubjectType(subject);
        object? instance = InstanceOf(subject);
        Rule? allow = null;
        foreach (Rule rule in RulesFor(action, subjectType, field))
        {
            if (match == FieldMatch.Named && !rule.Inverted && rule.Fields is not { Count: > 0 })
            {
                continue;
            }

            if (!Matches(rule, subjectType, instance))
            {
                continue;
            }

            if (_options.Semantics == RuleSemantics.Casl || rule.Inverted)
            {
                return rule; // CASL: first by priority decides. Deny-overrides: any matching deny decides.
            }

            allow ??= rule;
        }

        return allow;
    }

    /// <summary>Throws <see cref="ForbiddenException"/> unless allowed, with the deciding rule's reason or <paramref name="message"/>.</summary>
    public void EnsureCan(string action, object? subject = null, string? field = null, string? message = null)
    {
        if (UnlessCan(action, subject, field, message) is { } error)
        {
            throw error;
        }
    }

    /// <summary>The <see cref="ForbiddenException"/> <see cref="EnsureCan"/> would throw, or null when allowed.</summary>
    public ForbiddenException? UnlessCan(string action, object? subject = null, string? field = null, string? message = null)
    {
        Rule? rule = RelevantRuleFor(action, subject, field);
        if (rule is { Inverted: false })
        {
            return null;
        }

        ForbiddenException error = new(action, subject, DetectSubjectType(subject), field, rule?.Reason);
        error.SetMessage(message
            ?? (string.IsNullOrEmpty(rule?.Reason) ? null : rule.Reason)
            ?? _options.ForbiddenMessage?.Invoke(error)
            ?? $"Cannot execute \"{action}\" on \"{error.SubjectType}\"");
        return error;
    }

    // ---- rule lookup ----

    /// <summary>Rules for an action and subject type, highest priority first, without field filtering.</summary>
    public IReadOnlyList<Rule> PossibleRulesFor(string action, string? subjectType = null)
    {
        string type = subjectType ?? _options.AnySubjectType;
        return _byPriority
            .Where(r => r.Action.Contains(action) || r.Action.Contains(_options.AnyAction))
            .Where(r => r.Subject.Contains(type) || (type != _options.AnySubjectType && r.Subject.Contains(_options.AnySubjectType)))
            .ToList();
    }

    /// <summary>Rules for an action, subject type and optional field, highest priority first.</summary>
    public IReadOnlyList<Rule> RulesFor(string action, string? subjectType = null, string? field = null)
    {
        IReadOnlyList<Rule> rules = PossibleRulesFor(action, subjectType);
        return _hasFieldRules ? rules.Where(r => r.MatchesField(field)).ToList() : rules;
    }

    /// <summary>Every action rules mention for a subject type, including those on the any-subject type.</summary>
    public IReadOnlyList<string> ActionsFor(string subjectType)
    {
        List<string> actions = [];
        if (_actionsBySubject.TryGetValue(subjectType, out List<string>? own))
        {
            actions.AddRange(own);
        }

        if (subjectType != _options.AnySubjectType && _actionsBySubject.TryGetValue(_options.AnySubjectType, out List<string>? any))
        {
            actions.AddRange(any.Where(a => !actions.Contains(a)));
        }

        return actions;
    }

    // ---- fields ----

    /// <summary>
    /// Fields the user may act on, for an instance or a subject type. <paramref name="fieldsFrom"/> says which fields a
    /// rule covers; by default a rule's own fields, or for rules without fields every field the strict registry lists.
    /// </summary>
    public IReadOnlyList<string> PermittedFields(string action, object subject, Func<Rule, IEnumerable<string>>? fieldsFrom = null)
    {
        string subjectType = DetectSubjectType(subject);
        object? instance = InstanceOf(subject);
        fieldsFrom ??= rule => rule.Fields ?? DefaultFields(subjectType);
        IReadOnlyList<Rule> rules = PossibleRulesFor(action, subjectType);
        List<string> permitted = [];

        void Toggle(Rule rule)
        {
            foreach (string field in fieldsFrom(rule))
            {
                if (rule.Inverted)
                {
                    permitted.Remove(field);
                }
                else if (!permitted.Contains(field))
                {
                    permitted.Add(field);
                }
            }
        }

        if (_options.Semantics == RuleSemantics.Casl)
        {
            for (int i = rules.Count - 1; i >= 0; i--) // lowest priority first, so later rules have the last word
            {
                if (Matches(rules[i], subjectType, instance))
                {
                    Toggle(rules[i]);
                }
            }
        }
        else
        {
            foreach (Rule rule in rules.Where(r => !r.Inverted).Reverse().Concat(rules.Where(r => r.Inverted)))
            {
                if (Matches(rule, subjectType, instance))
                {
                    Toggle(rule);
                }
            }
        }

        return permitted;
    }

    /// <summary>
    /// Plain values from allow rules' conditions, as defaults for a new object (CASL's <c>rulesToFields</c>):
    /// <c>{ authorId: 5 }</c> gives <c>{ "authorId": 5 }</c>. Operator expressions are skipped; dotted keys nest.
    /// </summary>
    public JsonObject DefaultValues(string action, string subjectType)
    {
        JsonObject values = [];
        foreach (Rule rule in RulesFor(action, subjectType))
        {
            if (rule.Inverted || rule.Conditions is null)
            {
                continue;
            }

            foreach ((string key, JsonNode? value) in rule.Conditions)
            {
                if (value is not JsonObject)
                {
                    SetByPath(values, key, value?.DeepClone());
                }
            }
        }

        return values;
    }

    // ---- queries ----

    /// <summary>
    /// The rows the user may act on, as a predicate EF Core translates to SQL:
    /// <c>db.Posts.Where(ability.Filter&lt;Post&gt;("read"))</c>. Rule values become SQL parameters.
    /// </summary>
    public Expression<Func<T, bool>> Filter<T>(string action) =>
        (Expression<Func<T, bool>>)_filters.GetOrAdd((action, typeof(T)), key => BuildFilter(key.Action, key.Type));

    /// <summary>
    /// The rules for an action and subject type as one MongoDB query, or null when nothing is allowed and an empty
    /// object when everything is. Useful for document databases and search engines.
    /// </summary>
    public JsonObject? ToMongoQuery(string action, string subjectType) =>
        Combine<JsonNode>(
            RulesFor(action, subjectType),
            rule => rule.Inverted ? new JsonObject { ["$nor"] = new JsonArray(rule.Conditions!.DeepClone()) } : rule.Conditions!.DeepClone(),
            all => all.Count == 1 ? all[0].DeepClone() : new JsonObject { ["$and"] = new JsonArray(all.Select(n => (JsonNode?)n.DeepClone()).ToArray()) },
            any => any.Count == 1 ? any[0].DeepClone() : new JsonObject { ["$or"] = new JsonArray(any.Select(n => (JsonNode?)n.DeepClone()).ToArray()) },
            () => new JsonObject()) as JsonObject;

    /// <summary>
    /// Turns rules (highest priority first, as <see cref="RulesFor"/> returns them) into one condition, the way
    /// CASL's <c>rulesToCondition</c> does: each allow rule is bounded by the <c>cannot</c> rules ranked above it.
    /// Returns null when nothing is allowed, <paramref name="empty"/> when everything is.
    /// </summary>
    public static TResult? RulesToCondition<TResult>(
        IReadOnlyList<Rule> rules,
        Func<Rule, TResult> convert,
        Func<IReadOnlyList<TResult>, TResult> and,
        Func<IReadOnlyList<TResult>, TResult> or,
        Func<TResult> empty) where TResult : class
    {
        List<TResult> higherCannots = [];
        List<TResult> orConditions = [];
        bool unconditionalCan = false;

        foreach (Rule rule in rules)
        {
            if (rule.Inverted)
            {
                if (rule.Condition is null)
                {
                    break; // an unconditional cannot hides everything ranked below it
                }

                higherCannots.Add(convert(rule));
            }
            else
            {
                if (rule.Condition is null)
                {
                    unconditionalCan = true;
                    break;
                }

                TResult condition = convert(rule);
                orConditions.Add(higherCannots.Count > 0 ? and([condition, .. higherCannots]) : condition);
            }
        }

        if (unconditionalCan)
        {
            if (higherCannots.Count == 0)
            {
                return empty();
            }

            if (orConditions.Count == 0)
            {
                return and(higherCannots);
            }

            orConditions.Add(and(higherCannots));
        }

        return orConditions.Count == 0 ? null : or(orConditions);
    }

    // ---- browser ----

    /// <summary>
    /// The rules for @casl/ability in the browser (use the <c>casl-net</c> npm package to create the ability).
    /// Actions include their aliases. With deny-overrides, allow rules come first and <c>cannot</c> rules last, so
    /// CASL's "last rule wins" reaches the same answers. Conditions are adjusted where CASL's matcher departs from
    /// MongoDB semantics.
    /// </summary>
    public IReadOnlyList<RawRule> ToCaslRules()
    {
        IEnumerable<Rule> ordered = _options.Semantics == RuleSemantics.DenyOverrides
            ? _rules.Where(r => !r.Inverted).Concat(_rules.Where(r => r.Inverted))
            : _rules;
        return ordered.Select(r => CaslExport.Normalize(r.Origin with { Action = r.Action })).ToList();
    }

    /// <summary>The subject type of a check's subject.</summary>
    public string DetectSubjectType(object? subject) => subject switch
    {
        null => _options.AnySubjectType,
        string name => name,
        SubjectInstance tagged => tagged.Type,
        Type type => _subjects.Get(type).Name,
        _ => _options.DetectSubjectType?.Invoke(subject) ?? _subjects.Get(subject.GetType()).Name,
    };

    // ---- internals ----

    private static object? InstanceOf(object? subject) => subject switch
    {
        null or string or Type => null,
        SubjectInstance tagged => tagged.Value,
        _ => subject,
    };

    /// <summary>CASL's matchesConditions: for a type-level check, allow rules match and conditional cannot rules don't.</summary>
    private bool Matches(Rule rule, string subjectType, object? instance)
    {
        if (rule.Condition is null)
        {
            return true;
        }

        if (instance is null)
        {
            return !rule.Inverted;
        }

        Func<object, bool> check = _checks.GetOrAdd((rule, subjectType, instance.GetType()), key =>
        {
            SubjectDefinition definition = _subjects.For(key.Subject, key.Type);
            ParameterExpression obj = Expression.Parameter(typeof(object), "obj");
            ParameterExpression x = Expression.Variable(key.Type, "x");
            Expression body = new ExpressionBuilder(definition, nullSafe: true).Build(key.Rule.Condition!, x);
            return Expression.Lambda<Func<object, bool>>(
                Expression.Block([x], Expression.Assign(x, Expression.Convert(obj, key.Type)), body), obj).Compile();
        });
        return check(instance);
    }

    private LambdaExpression BuildFilter(string action, Type type)
    {
        SubjectDefinition definition = _subjects.Get(type);
        ParameterExpression x = Expression.Parameter(type, "x");
        ExpressionBuilder builder = new(definition, nullSafe: false);
        IReadOnlyList<Rule> rules = RulesFor(action, definition.Name);

        Expression? body = _options.Semantics == RuleSemantics.Casl
            ? Combine<Expression>(
                rules,
                rule => rule.Inverted ? Expression.Not(builder.Build(rule.Condition!, x)) : builder.Build(rule.Condition!, x),
                all => all.Aggregate(Expression.AndAlso),
                any => any.Aggregate(Expression.OrElse),
                () => Expression.Constant(true))
            : DenyOverridesFilter(rules, builder, x);

        return Expression.Lambda(body ?? Expression.Constant(false), x);
    }

    private static Expression? DenyOverridesFilter(IReadOnlyList<Rule> rules, ExpressionBuilder builder, ParameterExpression x)
    {
        Expression? Any(IEnumerable<Rule> subset)
        {
            Expression? result = null;
            foreach (Rule rule in subset)
            {
                if (rule.Condition is null)
                {
                    return Expression.Constant(true);
                }

                Expression part = builder.Build(rule.Condition, x);
                result = result is null ? part : Expression.OrElse(result, part);
            }

            return result;
        }

        Expression? allow = Any(rules.Where(r => !r.Inverted));
        if (allow is null)
        {
            return null;
        }

        Expression? deny = Any(rules.Where(r => r.Inverted)); // RulesFor already dropped cannot rules limited to fields
        return deny is null ? allow : Expression.AndAlso(allow, Expression.Not(deny));
    }

    private static TResult? Combine<TResult>(IReadOnlyList<Rule> rules, Func<Rule, TResult> convert,
        Func<IReadOnlyList<TResult>, TResult> and, Func<IReadOnlyList<TResult>, TResult> or, Func<TResult> empty) where TResult : class =>
        RulesToCondition(rules, convert, and, or, empty);

    private IReadOnlyList<string> DefaultFields(string subjectType) =>
        _subjects.TryGet(subjectType, out SubjectDefinition? definition) && definition!.Fields.Count > 0
            ? definition.Fields
            : throw new CaslConfigurationException(
                $"A rule on '{subjectType}' has no fields, so its fields can't be listed. Register the subject's fields, or pass fieldsFrom.");

    private static Dictionary<string, List<string>> IndexActions(IReadOnlyList<Rule> byPriority)
    {
        Dictionary<string, List<string>> index = new(StringComparer.Ordinal);
        foreach (Rule rule in byPriority)
        {
            foreach (string subject in rule.Subject)
            {
                if (!index.TryGetValue(subject, out List<string>? actions))
                {
                    index[subject] = actions = [];
                }

                foreach (string action in rule.Action.Where(a => !actions.Contains(a)))
                {
                    actions.Add(action);
                }
            }
        }

        return index;
    }

    private static readonly HashSet<string> ForbiddenKeys = ["__proto__", "constructor", "prototype"];

    private static void SetByPath(JsonObject target, string path, JsonNode? value)
    {
        string[] keys = path.Split('.');
        JsonObject current = target;
        foreach (string key in keys[..^1])
        {
            if (ForbiddenKeys.Contains(key))
            {
                continue; // CASL skips prototype-polluting keys; kept for identical output
            }

            if (current[key] is not JsonObject next)
            {
                next = [];
                current[key] = next;
            }

            current = next;
        }

        if (!ForbiddenKeys.Contains(keys[^1]))
        {
            current[keys[^1]] = value;
        }
    }
}

/// <summary>Thrown by <see cref="Ability.EnsureCan"/>. CASL's <c>ForbiddenError</c>.</summary>
public sealed class ForbiddenException : Exception
{
    private string _message = string.Empty;

    internal ForbiddenException(string action, object? subject, string subjectType, string? field, string? reason)
    {
        Action = action;
        Subject = subject;
        SubjectType = subjectType;
        Field = field;
        Reason = reason;
    }

    public string Action { get; }
    public object? Subject { get; }
    public string SubjectType { get; }
    public string? Field { get; }
    public string? Reason { get; }

    public override string Message => _message;

    internal void SetMessage(string message) => _message = message;
}

/// <summary>Which allow rules can grant a field.</summary>
public enum FieldMatch
{
    /// <summary>Any matching rule: one without a field list covers every field (CASL's behaviour).</summary>
    Any,

    /// <summary>Only rules that list the field (or a pattern matching it).</summary>
    Named,
}

public static class QueryableCaslExtensions
{
    /// <summary>Only the rows the ability allows for <paramref name="action"/>.</summary>
    public static IQueryable<T> AccessibleBy<T>(this IQueryable<T> query, Ability ability, string action = "read") =>
        query.Where(ability.Filter<T>(action));
}
