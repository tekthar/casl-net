using System.Linq.Expressions;
using System.Reflection;

namespace Casl;

/// <summary>
/// Maps subject names to CLR types and lists, per subject, which member paths rules may use in conditions
/// and which fields field-level rules may name. Anything not listed is rejected: rule authors can't filter on
/// <c>PasswordHash</c>, and a typo fails loudly instead of silently matching nothing.
/// </summary>
public sealed class SubjectRegistry
{
    private readonly Dictionary<string, SubjectDefinition> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, SubjectDefinition> _byType = [];

    public const string AllSubjects = "all";
    public const string AllActions = "manage";

    public SubjectRegistry Add<T>(string name, Action<SubjectBuilder<T>>? configure = null)
    {
        if (name == AllSubjects)
        {
            throw new CaslConfigurationException($"'{AllSubjects}' is reserved.");
        }

        SubjectBuilder<T> builder = new();
        configure?.Invoke(builder);
        SubjectDefinition definition = new(name, typeof(T), builder.ConditionPaths, builder.FieldNames);
        _byName.Add(name, definition);
        _byType.Add(typeof(T), definition);
        return this;
    }

    public IEnumerable<SubjectDefinition> Subjects => _byName.Values;

    public SubjectDefinition Get(string name) =>
        _byName.TryGetValue(name, out SubjectDefinition? d) ? d : throw new CaslConfigurationException($"Unknown subject '{name}'.");

    public SubjectDefinition Get(Type type) =>
        _byType.TryGetValue(type, out SubjectDefinition? d) ? d : throw new CaslConfigurationException($"Type '{type.Name}' is not a registered subject.");

    public bool IsKnown(string name) => name == AllSubjects || _byName.ContainsKey(name);
}

public sealed class SubjectDefinition
{
    internal SubjectDefinition(string name, Type type, IReadOnlyCollection<string> conditionPaths, IReadOnlyCollection<string> fields)
    {
        Name = name;
        Type = type;
        ConditionPaths = new HashSet<string>(conditionPaths, StringComparer.Ordinal);
        Fields = fields.ToList();
        foreach (string path in ConditionPaths)
        {
            _ = MemberPath.Resolve(type, path); // fail at registration, not at request time
        }
    }

    public string Name { get; }
    public Type Type { get; }

    /// <summary>Member paths (camelCase, dotted for navigations) allowed in conditions.</summary>
    public IReadOnlySet<string> ConditionPaths { get; }

    /// <summary>Fields that field-level rules may name; also the "all fields" set for permitted-field checks.</summary>
    public IReadOnlyList<string> Fields { get; }
}

public sealed class SubjectBuilder<T>
{
    internal List<string> ConditionPaths { get; } = [];
    internal List<string> FieldNames { get; } = [];

    /// <summary>Allow conditions on these member paths, e.g. "projectId", "project.orgUnitId", "labels".</summary>
    public SubjectBuilder<T> ConditionFields(params string[] paths)
    {
        ConditionPaths.AddRange(paths);
        return this;
    }

    /// <summary>Allow a member in conditions, using its camelCase name.</summary>
    public SubjectBuilder<T> ConditionField<TValue>(Expression<Func<T, TValue>> member)
    {
        ConditionPaths.Add(MemberPath.FromExpression(member));
        return this;
    }

    /// <summary>Fields that field-level rules can grant or deny, e.g. "summary", "status".</summary>
    public SubjectBuilder<T> Fields(params string[] fields)
    {
        FieldNames.AddRange(fields);
        return this;
    }
}

/// <summary>Resolves dotted camelCase paths ("project.orgUnitId") to CLR members.</summary>
internal static class MemberPath
{
    public static IReadOnlyList<MemberInfo> Resolve(Type root, string path)
    {
        List<MemberInfo> members = [];
        Type current = root;
        foreach (string segment in path.Split('.'))
        {
            MemberInfo member = (MemberInfo?)current.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                ?? current.GetField(segment, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                ?? throw new CaslConfigurationException($"'{root.Name}' has no member '{path}' (segment '{segment}').");
            members.Add(member);
            current = member is PropertyInfo p ? p.PropertyType : ((FieldInfo)member).FieldType;
            current = ExpressionBuilder.ElementTypeOf(current) ?? current; // "comments.authorId" steps into the element
        }

        return members;
    }

    /// <summary>
    /// For a path that passes through a collection ("comments.authorId"), the collection part and the rest
    /// ("comments", "authorId"). MongoDB reads such a path as "any element matches". Null when there is none.
    /// </summary>
    public static (string Collection, string Remainder)? SplitAtCollection(Type root, string path)
    {
        string[] segments = path.Split('.');
        IReadOnlyList<MemberInfo> members = Resolve(root, path);
        for (int i = 0; i < members.Count - 1; i++)
        {
            if (ExpressionBuilder.ElementTypeOf(TypeOf(members[i])) is not null)
            {
                return (string.Join('.', segments[..(i + 1)]), string.Join('.', segments[(i + 1)..]));
            }
        }

        return null;
    }

    public static Type TypeOf(MemberInfo member) => member is PropertyInfo p ? p.PropertyType : ((FieldInfo)member).FieldType;

    public static string FromExpression(LambdaExpression expression)
    {
        List<string> names = [];
        Expression? node = expression.Body;
        while (node is MemberExpression m)
        {
            names.Insert(0, char.ToLowerInvariant(m.Member.Name[0]) + m.Member.Name[1..]);
            node = m.Expression;
        }

        return names.Count > 0 ? string.Join('.', names) : throw new CaslConfigurationException("Use a member access such as x => x.ProjectId.");
    }
}
