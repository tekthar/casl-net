using System.Collections;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Casl;

/// <summary>
/// Turns a condition tree into a LINQ expression over a subject type.
///
/// Two modes, same semantics:
/// - Query mode is for EF Core. Member chains are plain (SQL has no null references), and every value
///   sits in a <see cref="ValueBox{T}"/> so EF sends it as a parameter: one cached SQL plan serves all users.
/// - In-memory mode adds null checks along navigations so a missing related object reads as null,
///   the way a LEFT JOIN does in SQL.
///
/// <paramref name="forSql"/> marks the expression for a database (<see cref="Ability.Filter{T}"/>), which refuses an
/// index into a collection of objects: related rows have no order in SQL.
/// </summary>
internal sealed class ExpressionBuilder(SubjectDefinition subject, bool nullSafe, TimeSpan? regexTimeout = null, bool forSql = false)
{
    private static readonly MethodInfo EnumerableAny = typeof(Enumerable).GetMethods()
        .Single(m => m.Name == nameof(Enumerable.Any) && m.GetParameters().Length == 2);

    private static readonly MethodInfo EnumerableContains = typeof(Enumerable).GetMethods()
        .Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2);

    private static readonly MethodInfo EnumerableCount = typeof(Enumerable).GetMethods()
        .Single(m => m.Name == nameof(Enumerable.Count) && m.GetParameters().Length == 1);

    private static readonly MethodInfo EnumerableElementAtOrDefault = typeof(Enumerable).GetMethods()
        .Single(m => m.Name == nameof(Enumerable.ElementAtOrDefault) && m.GetParameters().Length == 2 && m.GetParameters()[1].ParameterType == typeof(int));

    private static readonly MethodInfo RegexIsMatch = typeof(System.Text.RegularExpressions.Regex)
        .GetMethod(nameof(System.Text.RegularExpressions.Regex.IsMatch), [typeof(string), typeof(string)])!;

    private static readonly MethodInfo RegexInstanceIsMatch = typeof(System.Text.RegularExpressions.Regex)
        .GetMethod(nameof(System.Text.RegularExpressions.Regex.IsMatch), [typeof(string)])!;

    private static readonly MethodInfo RegexIsMatchWithOptions = typeof(System.Text.RegularExpressions.Regex)
        .GetMethod(nameof(System.Text.RegularExpressions.Regex.IsMatch), [typeof(string), typeof(string), typeof(System.Text.RegularExpressions.RegexOptions)])!;

    public Expression Build(Condition condition, Expression target, string pathPrefix = "") => condition switch
    {
        AndCondition and => Combine(and.Items.Select(i => Build(i, target, pathPrefix)).ToList(), Expression.AndAlso),
        OrCondition or => Combine(or.Items.Select(i => Build(i, target, pathPrefix)).ToList(), Expression.OrElse),
        NotCondition not => Expression.Not(Build(not.Inner, target, pathPrefix)),
        FieldCondition field => BuildField(field, target, pathPrefix),
        ElemMatchCondition elem => BuildElemMatch(elem, target, pathPrefix),
        _ => throw new CaslConfigurationException($"Unsupported condition {condition.GetType().Name}."),
    };

    /// <summary>
    /// Joins conditions with <c>&amp;&amp;</c> or <c>||</c> as a balanced tree, keeping their order. A left-deep chain
    /// would be as deep as the list is long, and EF Core walks expression trees recursively: a rule with tens of
    /// thousands of alternatives would overflow the stack, which ends the whole process. Balanced, the depth is log2(n).
    /// </summary>
    internal static Expression Combine(IReadOnlyList<Expression> items, Func<Expression, Expression, Expression> join) =>
        items.Count > 0
            ? Combine(items, 0, items.Count, join)
            : throw new CaslConfigurationException("A list of conditions can't be empty.");

    private static Expression Combine(IReadOnlyList<Expression> items, int start, int count, Func<Expression, Expression, Expression> join)
    {
        if (count == 1)
        {
            return items[start];
        }

        int half = count / 2;
        return join(Combine(items, start, half, join), Combine(items, start + half, count - half, join));
    }

    private Expression BuildField(FieldCondition field, Expression target, string pathPrefix)
    {
        string fullPath = Join(pathPrefix, field.Path);
        CheckAllowed(fullPath); // before touching members, so unknown and forbidden paths get the same answer

        if (field.Path.Length > 0 && MemberPath.SplitAtCollection(target.Type, field.Path) is var (collection, rest))
        {
            // MongoDB dot notation through an array: { "comments.authorId": "u2" } = some comment by u2.
            return BuildElemMatch(new ElemMatchCondition(collection, field with { Path = rest }), target, pathPrefix);
        }

        (Expression member, Type memberType) = field.Path.Length == 0 ? (target, target.Type) : Access(target, field.Path);

        if (ElementTypeOf(memberType) is { } elementType)
        {
            // MongoDB semantics on arrays: { labels: "bug" } means "labels contains bug".
            ParameterExpression e = Expression.Parameter(elementType, "e");
            return field.Operator switch
            {
                "$exists" => Exists(member, memberType, field.Value!.GetValue<bool>()),
                "$size" => Size(member, elementType, field.Value, fullPath),
                "$ne" => Expression.Not(AnyOf(member, e, Compare(e, elementType, "$eq", field.Value, fullPath))),
                "$nin" => Expression.Not(AnyOf(member, e, Compare(e, elementType, "$in", field.Value, fullPath))),
                _ => AnyOf(member, e, Compare(e, elementType, field.Operator, field.Value, fullPath)),
            };
        }

        return Compare(member, memberType, field.Operator, field.Value, fullPath);
    }

    private Expression BuildElemMatch(ElemMatchCondition elem, Expression target, string pathPrefix)
    {
        string fullPath = Join(pathPrefix, elem.Path);
        if (!subject.AllowsUnder(fullPath))
        {
            CheckAllowed(fullPath); // before touching members, as in BuildField
        }

        (Expression member, Type memberType) = Access(target, elem.Path);
        Type elementType = ElementTypeOf(memberType)
            ?? throw new CaslConfigurationException($"'$elemMatch' needs a collection, but '{fullPath}' is {memberType.Name}.");
        ParameterExpression e = Expression.Parameter(elementType, "e");
        return AnyOf(member, e, Build(elem.Inner, e, fullPath));
    }

    private Expression Compare(Expression member, Type memberType, string op, JsonNode? value, string path)
    {
        switch (op)
        {
            case "$exists":
                return Exists(member, memberType, value!.GetValue<bool>());
            case "$eq":
                return value is null ? IsNull(member, memberType, path) : Expression.Equal(member, Boxed(value, memberType, path));
            case "$ne":
                return Expression.Not(Compare(member, memberType, "$eq", value, path));
            case "$in":
            case "$nin":
                Expression contains = In(member, memberType, (JsonArray)value!, path);
                return op == "$in" ? contains : Expression.Not(contains);
            case "$regex":
                return Regex(member, memberType, (JsonObject)value!, path);
            case "$size":
                throw new CaslConfigurationException($"'$size' needs a collection, but '{path}' is {memberType.Name}.");
            case "$gt" or "$gte" or "$lt" or "$lte":
                Type core = Nullable.GetUnderlyingType(memberType) ?? memberType;
                if (core == typeof(string) || core == typeof(bool) || core == typeof(Guid) || core.IsEnum)
                {
                    throw new CaslConfigurationException($"'{op}' isn't supported on '{path}' ({core.Name}); ordering of text differs between .NET and databases.");
                }

                if (value is null)
                {
                    return Expression.Constant(false); // MongoDB: comparisons with null never match
                }

                Expression right = Boxed(value, memberType, path);
                return op switch
                {
                    "$gt" => Expression.GreaterThan(member, right),
                    "$gte" => Expression.GreaterThanOrEqual(member, right),
                    "$lt" => Expression.LessThan(member, right),
                    _ => Expression.LessThanOrEqual(member, right),
                };
            default:
                throw new CaslConfigurationException($"Operator '{op}' is not supported.");
        }
    }

    private Expression In(Expression member, Type memberType, JsonArray values, string path)
    {
        Type listType = typeof(List<>).MakeGenericType(memberType);
        IList list = (IList)Activator.CreateInstance(listType)!;
        foreach (JsonNode? value in values)
        {
            list.Add(value is null ? null : ValueConverter.Convert(value, memberType, path));
        }

        Expression boxed = Box(list, listType);
        return Expression.Call(EnumerableContains.MakeGenericMethod(memberType), boxed, member);
    }

    private Expression Size(Expression collection, Type elementType, JsonNode? value, string path)
    {
        int size = value is JsonValue v && v.TryGetValue(out int n) && n >= 0
            ? n
            : throw new CaslConfigurationException($"'$size' on '{path}' needs a non-negative whole number.");
        Expression count = Expression.Equal(Expression.Call(EnumerableCount.MakeGenericMethod(elementType), AsEnumerable(collection, elementType)), Box(size, typeof(int)));
        return nullSafe ? Expression.AndAlso(Expression.NotEqual(collection, Expression.Constant(null, collection.Type)), count) : count;
    }

    /// <summary>
    /// <c>{ $regex, $options }</c>. EF Core translates <c>Regex.IsMatch</c> on PostgreSQL and SQLite (options only on
    /// PostgreSQL); other providers may not translate it. See <see cref="PortableRegex"/> for what's refused or rewritten.
    /// </summary>
    private Expression Regex(Expression member, Type memberType, JsonObject spec, string path)
    {
        if (memberType != typeof(string))
        {
            throw new CaslConfigurationException($"'$regex' needs text, but '{path}' is {memberType.Name}.");
        }

        string pattern = spec["pattern"]?.GetValue<string>() ?? throw new CaslConfigurationException($"'$regex' on '{path}' needs a pattern.");
        string flags = spec["options"]?.GetValue<string>() ?? string.Empty;
        System.Text.RegularExpressions.RegexOptions options = System.Text.RegularExpressions.RegexOptions.None;
        foreach (char flag in flags)
        {
            options |= flag switch
            {
                'i' => System.Text.RegularExpressions.RegexOptions.IgnoreCase,
                'm' => System.Text.RegularExpressions.RegexOptions.Multiline,
                's' => System.Text.RegularExpressions.RegexOptions.Singleline,
                'x' => System.Text.RegularExpressions.RegexOptions.IgnorePatternWhitespace,
                _ => throw new CaslConfigurationException($"'$options' on '{path}' supports i, m, s and x, not '{flag}'."),
            };
        }

        try
        {
            _ = new System.Text.RegularExpressions.Regex(pattern, options);
        }
        catch (ArgumentException ex)
        {
            throw new CaslConfigurationException($"'$regex' on '{path}' is not a valid pattern: {ex.Message}");
        }

        // The same pattern runs in memory and in SQL, rewritten so .NET and the database agree.
        string portable = PortableRegex.Rewrite(pattern, options, path);
        Expression match;
        if (nullSafe)
        {
            // In memory: one prebuilt Regex per rule, with a match timeout, because rule authors choose the pattern.
            System.Text.RegularExpressions.Regex regex = new(portable, options, regexTimeout ?? System.Text.RegularExpressions.Regex.InfiniteMatchTimeout);
            match = Expression.Call(Expression.Constant(regex), RegexInstanceIsMatch, member);
        }
        else
        {
            match = options == System.Text.RegularExpressions.RegexOptions.None
                ? Expression.Call(RegexIsMatch, member, Box(portable, typeof(string)))
                : Expression.Call(RegexIsMatchWithOptions, member, Box(portable, typeof(string)), Expression.Constant(options));
        }
        // Also in SQL: without the guard, NULL REGEXP gives NULL, and NOT NULL stays NULL, so $nor would drop the row.
        return Expression.AndAlso(Expression.NotEqual(member, Expression.Constant(null, typeof(string))), match);
    }

    private static Expression AsEnumerable(Expression collection, Type elementType)
    {
        Type enumerable = typeof(IEnumerable<>).MakeGenericType(elementType);
        return collection.Type == enumerable ? collection : Expression.Convert(collection, enumerable);
    }

    private static Expression Exists(Expression member, Type memberType, bool exists)
    {
        if (memberType.IsValueType && Nullable.GetUnderlyingType(memberType) is null)
        {
            return Expression.Constant(exists);
        }

        Expression isNull = Expression.Equal(member, Expression.Constant(null, memberType));
        return exists ? Expression.Not(isNull) : isNull;
    }

    private static Expression IsNull(Expression member, Type memberType, string path) =>
        memberType.IsValueType && Nullable.GetUnderlyingType(memberType) is null
            ? Expression.Constant(false)
            : Expression.Equal(member, Expression.Constant(null, memberType));

    private Expression Boxed(JsonNode value, Type memberType, string path)
    {
        object converted = ValueConverter.Convert(value, memberType, path)!;
        return Box(converted, memberType);
    }

    /// <summary>A field read on a constant object: EF Core turns it into a SQL parameter.</summary>
    private static Expression Box(object? value, Type type)
    {
        object box = Activator.CreateInstance(typeof(ValueBox<>).MakeGenericType(type), value)!;
        return Expression.Field(Expression.Constant(box), nameof(ValueBox<int>.Value));
    }

    private Expression AnyOf(Expression collection, ParameterExpression element, Expression predicate)
    {
        Type elementType = element.Type;
        Expression source = collection.Type == typeof(IEnumerable<>).MakeGenericType(elementType)
            ? collection
            : Expression.Convert(collection, typeof(IEnumerable<>).MakeGenericType(elementType));
        Expression any = Expression.Call(EnumerableAny.MakeGenericMethod(elementType), source, Expression.Lambda(predicate, element));
        return nullSafe ? Expression.AndAlso(Expression.NotEqual(collection, Expression.Constant(null, collection.Type)), any) : any;
    }

    private (Expression Member, Type Type) Access(Expression target, string path)
    {
        Expression current = target;
        bool crossedNavigation = false;
        Expression? nullGuard = null;
        IReadOnlyList<PathSegment> segments = MemberPath.Resolve(target.Type, path);
        for (int i = 0; i < segments.Count; i++)
        {
            if (i > 0 && !current.Type.IsValueType)
            {
                crossedNavigation = true;
                Expression notNull = Expression.NotEqual(current, Expression.Constant(null, current.Type));
                nullGuard = nullGuard is null ? notNull : Expression.AndAlso(nullGuard, notNull);
            }

            if (forSql && segments[i].Index is not null && !IsSimpleValue(segments[i].Type))
            {
                // EF Core translates the index to LIMIT/OFFSET without an ORDER BY, so "first" would be whichever row the
                // database happens to return first, which changes as rows are updated, unlike the list order in memory.
                throw new CaslConfigurationException(
                    $"'{subject.Name}.{path}' indexes a collection of {segments[i].Type.Name}, and related rows have no order in SQL, so Filter and AccessibleBy can't tell which one is first. "
                    + "Use a path through the collection ('comments.authorId', any element) or $elemMatch, or check it in memory with Can.");
            }

            current = segments[i].Index is { } index
                ? Expression.Call(EnumerableElementAtOrDefault.MakeGenericMethod(segments[i].Type), AsEnumerable(current, segments[i].Type), Expression.Constant(index))
                : Expression.MakeMemberAccess(current, segments[i].Member!);
            if (segments[i].Index is not null)
            {
                crossedNavigation = true; // an index past the end reads as missing
            }
        }

        Type type = current.Type;
        if (!crossedNavigation)
        {
            return (current, type);
        }

        // Through a navigation, a missing related row reads as null (LEFT JOIN semantics).
        Type nullableType = type.IsValueType && Nullable.GetUnderlyingType(type) is null ? typeof(Nullable<>).MakeGenericType(type) : type;
        Expression value = type == nullableType ? current : Expression.Convert(current, nullableType);
        if (!nullSafe || nullGuard is null)
        {
            return (value, nullableType);
        }

        return (Expression.Condition(nullGuard, value, Expression.Constant(null, nullableType)), nullableType);
    }

    private void CheckAllowed(string fullPath)
    {
        if (!subject.Allows(fullPath))
        {
            throw new CaslConfigurationException($"Conditions on '{subject.Name}.{fullPath}' are not allowed. Register the path with ConditionFields(\"{fullPath}\").");
        }
    }

    private static string Join(string prefix, string path) =>
        prefix.Length == 0 ? path : path.Length == 0 ? prefix : prefix + "." + path;

    /// <summary>Whether <paramref name="type"/> is a single value (text, a number, a date...), not an object with members.</summary>
    private static bool IsSimpleValue(Type type)
    {
        Type core = Nullable.GetUnderlyingType(type) ?? type;
        return core.IsPrimitive || core.IsEnum || core == typeof(string) || core == typeof(decimal) || core == typeof(Guid)
            || core == typeof(DateTime) || core == typeof(DateTimeOffset) || core == typeof(DateOnly) || core == typeof(TimeOnly)
            || core == typeof(TimeSpan);
    }

    internal static Type? ElementTypeOf(Type type)
    {
        if (type == typeof(string))
        {
            return null;
        }

        Type? enumerable = type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>)
            ? type
            : type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        return enumerable?.GetGenericArguments()[0];
    }
}

/// <summary>Holds a rule value. Expressions read its field, which EF Core parameterizes.</summary>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
public sealed class ValueBox<T>(T value)
{
    /// <summary>The rule value.</summary>
    public readonly T Value = value;
}

internal static class ValueConverter
{
    public static object? Convert(JsonNode value, Type target, string path)
    {
        Type type = Nullable.GetUnderlyingType(target) ?? target;
        try
        {
            if (value is not JsonValue v)
            {
                throw new FormatException("expected a single value");
            }

            if (type == typeof(string))
            {
                return v.TryGetValue(out string? s) ? s : v.ToJsonString();
            }

            if (type == typeof(Guid))
            {
                return Guid.Parse(v.GetValue<string>());
            }

            if (type.IsEnum)
            {
                return v.TryGetValue(out string? name)
                    ? Enum.Parse(type, name, ignoreCase: true)
                    : Enum.ToObject(type, v.GetValue<long>());
            }

            if (type == typeof(bool))
            {
                return v.GetValue<bool>();
            }

            // A time value must mean the same instant on every server. Text with an offset ("+02:00", or what a
            // DateTimeOffset variable becomes) is kept in UTC: read as the server's local time, it would move a rule
            // such as { dueAt: { $gt: "${now}" } } by the server's UTC offset. Text without an offset is used as written.
            if (type == typeof(DateTime))
            {
                if (v.TryGetValue(out string? text))
                {
                    DateTime parsed = DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                    return parsed.Kind == DateTimeKind.Local ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture).UtcDateTime : parsed;
                }

                if (v.TryGetValue(out DateTimeOffset instant))
                {
                    return instant.UtcDateTime;
                }

                DateTime dt = v.GetValue<DateTime>();
                return dt.Kind == DateTimeKind.Local ? dt.ToUniversalTime() : dt;
            }

            if (type == typeof(DateTimeOffset))
            {
                // Without an offset, the text is UTC, not the server's local time.
                return v.TryGetValue(out string? text)
                    ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)
                    : v.GetValue<DateTimeOffset>();
            }

            if (type == typeof(DateOnly))
            {
                return DateOnly.Parse(v.GetValue<string>(), CultureInfo.InvariantCulture);
            }

            if (type.IsPrimitive || type == typeof(decimal))
            {
                JsonElement element = JsonSerializer.SerializeToElement(v);
                return element.ValueKind == JsonValueKind.String
                    ? System.Convert.ChangeType(element.GetString(), type, CultureInfo.InvariantCulture)
                    : System.Convert.ChangeType(element.GetDecimal(), type, CultureInfo.InvariantCulture);
            }

            throw new FormatException($"type {type.Name} isn't supported in conditions");
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or InvalidCastException or ArgumentException or OverflowException or JsonException)
        {
            throw new CaslConfigurationException($"Value {value.ToJsonString()} for '{path}' can't be used as {type.Name}: {ex.Message}.");
        }
    }
}
