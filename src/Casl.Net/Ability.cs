using System.Collections.Concurrent;
using System.Linq.Expressions;

namespace Casl;

/// <summary>
/// What one user may do: a set of rules with placeholders already filled in.
///
/// Semantics are deny-overrides: access is granted when at least one allow rule matches and no deny
/// ("cannot") rule matches. <see cref="ToCaslRules"/> orders rules so that @casl/ability in the browser,
/// which lets later rules win, reaches the same decisions.
///
/// Field-level rules follow CASL: a check without a field ignores deny rules that are limited to fields,
/// and counts allow rules that are limited to fields.
/// </summary>
public sealed class Ability
{
    private readonly SubjectRegistry _subjects;
    private readonly IReadOnlyList<CompiledRule> _rules;
    private readonly ConcurrentDictionary<(string Action, Type Type), LambdaExpression> _filters = new();
    private readonly ConcurrentDictionary<(CompiledRule Rule, Type Type), Func<object, bool>> _checks = new();

    internal Ability(SubjectRegistry subjects, IReadOnlyList<CompiledRule> rules)
    {
        _subjects = subjects;
        _rules = rules;
    }

    /// <summary>The rules with placeholders filled in, in the order they were added.</summary>
    public IReadOnlyList<RawRule> Rules => _rules.Select(r => r.Raw).ToList();

    /// <summary>
    /// Type-level check, for showing a menu or a button before any instance is loaded: true when some rule
    /// allows the action on the subject and no unconditional rule denies it.
    /// </summary>
    public bool Can(string action, string subject)
    {
        List<CompiledRule> relevant = Relevant(action, subject).ToList();
        return relevant.Any(r => !r.Raw.Inverted)
            && !relevant.Any(r => r.Raw.Inverted && r.Condition is null && r.Raw.Fields is null);
    }

    public bool Cannot(string action, string subject) => !Can(action, subject);

    /// <summary>Checks one object, optionally one of its fields.</summary>
    public bool Can(string action, object instance, string? field = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        SubjectDefinition subject = SubjectOf(instance);
        bool allowed = false;
        foreach (CompiledRule rule in Relevant(action, subject.Name))
        {
            if (!MatchesField(rule, field) || !Matches(rule, subject, instance))
            {
                continue;
            }

            if (rule.Raw.Inverted)
            {
                return false;
            }

            allowed = true;
        }

        return allowed;
    }

    public bool Cannot(string action, object instance, string? field = null) => !Can(action, instance, field);

    /// <summary>Throws <see cref="ForbiddenException"/> with the matching rule's reason when access is denied.</summary>
    public void EnsureCan(string action, object instance, string? field = null)
    {
        if (Can(action, instance, field))
        {
            return;
        }

        SubjectDefinition subject = SubjectOf(instance);
        string? reason = Relevant(action, subject.Name)
            .FirstOrDefault(r => r.Raw.Inverted && r.Raw.Reason is not null && MatchesField(r, field) && Matches(r, subject, instance))?.Raw.Reason;
        throw new ForbiddenException(action, subject.Name, field, reason);
    }

    /// <summary>
    /// The rows the user may act on, as a predicate EF Core translates to SQL:
    /// <c>db.WorkItems.Where(ability.Filter&lt;WorkItem&gt;("read"))</c>. Rule values become SQL parameters.
    /// </summary>
    public Expression<Func<T, bool>> Filter<T>(string action) =>
        (Expression<Func<T, bool>>)_filters.GetOrAdd((action, typeof(T)), key => BuildFilter(key.Action, _subjects.Get(key.Type), nullSafe: false));

    /// <summary>Fields of this instance the user may act on, in the subject's declared order.</summary>
    public IReadOnlyList<string> PermittedFields(string action, object instance)
    {
        SubjectDefinition subject = SubjectOf(instance);
        HashSet<string> permitted = [];
        List<CompiledRule> matching = Relevant(action, subject.Name).Where(r => Matches(r, subject, instance)).ToList();
        foreach (CompiledRule rule in matching.Where(r => !r.Raw.Inverted))
        {
            permitted.UnionWith(rule.Raw.Fields ?? subject.Fields);
        }

        foreach (CompiledRule rule in matching.Where(r => r.Raw.Inverted))
        {
            permitted.ExceptWith(rule.Raw.Fields ?? subject.Fields);
        }

        return subject.Fields.Where(permitted.Contains).ToList();
    }

    /// <summary>
    /// The rules for @casl/ability in the browser: allow rules first, deny rules last, so CASL's
    /// "later rule wins" gives the same deny-overrides answers as this class. Conditions are adjusted where
    /// CASL's matcher departs from MongoDB semantics. Create the browser ability with <c>casl-net</c>,
    /// which switches on CASL's logical operators ($and, $or, $nor, $not).
    /// </summary>
    public IReadOnlyList<RawRule> ToCaslRules() =>
        _rules.Where(r => !r.Raw.Inverted).Concat(_rules.Where(r => r.Raw.Inverted)).Select(r => CaslExport.Normalize(r.Raw)).ToList();

    private LambdaExpression BuildFilter(string action, SubjectDefinition subject, bool nullSafe)
    {
        ParameterExpression x = Expression.Parameter(subject.Type, "x");
        ExpressionBuilder builder = new(subject, nullSafe);
        List<CompiledRule> relevant = Relevant(action, subject.Name).ToList();

        Expression? allow = Combine(relevant.Where(r => !r.Raw.Inverted), builder, x);
        if (allow is null)
        {
            return Expression.Lambda(Expression.Constant(false), x);
        }

        // Deny rules limited to fields don't hide rows; they only restrict those fields.
        Expression? deny = Combine(relevant.Where(r => r.Raw.Inverted && r.Raw.Fields is null), builder, x);
        Expression body = deny is null ? allow : Expression.AndAlso(allow, Expression.Not(deny));
        return Expression.Lambda(body, x);
    }

    private static Expression? Combine(IEnumerable<CompiledRule> rules, ExpressionBuilder builder, ParameterExpression x)
    {
        Expression? result = null;
        foreach (CompiledRule rule in rules)
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

    private bool Matches(CompiledRule rule, SubjectDefinition subject, object instance)
    {
        if (rule.Condition is null)
        {
            return true;
        }

        Func<object, bool> check = _checks.GetOrAdd((rule, subject.Type), key =>
        {
            ParameterExpression obj = Expression.Parameter(typeof(object), "obj");
            ParameterExpression x = Expression.Variable(key.Type, "x");
            Expression body = new ExpressionBuilder(subject, nullSafe: true).Build(key.Rule.Condition!, x);
            return Expression.Lambda<Func<object, bool>>(
                Expression.Block([x], Expression.Assign(x, Expression.Convert(obj, key.Type)), body), obj).Compile();
        });
        return check(instance);
    }

    private static bool MatchesField(CompiledRule rule, string? field) =>
        rule.Raw.Fields is null || (field is null ? !rule.Raw.Inverted : rule.Raw.Fields.Contains(field));

    private IEnumerable<CompiledRule> Relevant(string action, string subject) =>
        _rules.Where(r => (r.Actions.Contains(action) || r.Actions.Contains(SubjectRegistry.AllActions))
                       && (r.Subjects.Contains(subject) || r.Subjects.Contains(SubjectRegistry.AllSubjects)));

    private SubjectDefinition SubjectOf(object instance)
    {
        for (Type? type = instance.GetType(); type is not null; type = type.BaseType)
        {
            try
            {
                return _subjects.Get(type);
            }
            catch (CaslConfigurationException) when (type.BaseType is not null)
            {
                // EF Core proxies derive from the entity type
            }
        }

        throw new CaslConfigurationException($"Type '{instance.GetType().Name}' is not a registered subject.");
    }
}

internal sealed record CompiledRule(RawRule Raw, HashSet<string> Actions, HashSet<string> Subjects, Condition? Condition);

public sealed class ForbiddenException(string action, string subject, string? field, string? reason)
    : Exception(reason ?? (field is null ? $"You can't {action} this {subject}." : $"You can't {action} '{field}' on this {subject}."))
{
    public string Action { get; } = action;
    public string Subject { get; } = subject;
    public string? Field { get; } = field;
    public string? Reason { get; } = reason;
}

public static class QueryableCaslExtensions
{
    /// <summary>Only the rows the ability allows for <paramref name="action"/>.</summary>
    public static IQueryable<T> AccessibleBy<T>(this IQueryable<T> query, Ability ability, string action = "read") =>
        query.Where(ability.Filter<T>(action));
}
