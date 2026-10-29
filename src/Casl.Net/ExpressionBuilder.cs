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
/// </summary>
internal sealed class ExpressionBuilder(SubjectDefinition subject, bool nullSafe)
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

    private static readonly MethodInfo RegexIsMatchWithOptions = typeof(System.Text.RegularExpressions.Regex)
        .GetMethod(nameof(System.Text.RegularExpressions.Regex.IsMatch), [typeof(string), typeof(string), typeof(System.Text.RegularExpressions.RegexOptions)])!;

    public Expression Build(Condition condition, Expression target, string pathPrefix = "") => condition switch
    {
        AndCondition and => and.Items.Select(i => Build(i, target, pathPrefix)).Aggregate(Expression.AndAlso),
        OrCondition or => or.Items.Select(i => Build(i, target, pathPrefix)).Aggregate(Expression.OrElse),
        NotCondition not => Expression.Not(Build(not.Inner, target, pathPrefix)),
        FieldCondition field => BuildField(field, target, pathPrefix),
        ElemMatchCondition elem => BuildElemMatch(elem, target, pathPrefix),
        _ => throw new CaslConfigurationException($"Unsupported condition {condition.GetType().Name}."),
    };

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
        Expression match = options == System.Text.RegularExpressions.RegexOptions.None
            ? Expression.Call(RegexIsMatch, member, Box(portable, typeof(string)))
            : Expression.Call(RegexIsMatchWithOptions, member, Box(portable, typeof(string)), Expression.Constant(options));
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

            if (type == typeof(DateTime))
            {
                return v.TryGetValue(out DateTime dt) ? dt : DateTime.Parse(v.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            }

            if (type == typeof(DateTimeOffset))
            {
                return v.TryGetValue(out DateTimeOffset dto) ? dto : DateTimeOffset.Parse(v.GetValue<string>(), CultureInfo.InvariantCulture);
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
