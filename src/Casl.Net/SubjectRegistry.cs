using System.Linq.Expressions;
using System.Reflection;

namespace Casl;

/// <summary>
/// Maps subject names to CLR types.
///
/// A <b>strict</b> registry (<c>new SubjectRegistry().Add&lt;T&gt;(...)</c>) lists, per subject, which member paths
/// rules may use in conditions and which fields field-level rules may name. Anything else is rejected: rule
/// authors can't filter on <c>PasswordHash</c>, and a typo fails loudly. Use it when rules come from users.
///
/// An <b>open</b> registry (<see cref="Open"/>, the default when no registry is given) behaves like CASL:
/// any subject name is accepted, an object's subject type is its class name, and conditions may use any public
/// member. Use it when rules are written by developers.
/// </summary>
public sealed class SubjectRegistry
{
    private readonly Dictionary<string, SubjectDefinition> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, SubjectDefinition> _byType = [];
    private readonly Dictionary<Type, SubjectDefinition> _openByType = [];
    private readonly object _gate = new();

    /// <summary>The default name of the subject type that means every subject, as in CASL.</summary>
    public const string AllSubjects = "all";

    /// <summary>The default name of the action that means every action, as in CASL.</summary>
    public const string AllActions = "manage";

    /// <summary>A CASL-style registry: every subject name is valid and conditions may use any public member.</summary>
    public static SubjectRegistry Open() => new() { IsOpen = true };

    /// <summary>True for an open registry (<see cref="Open"/>); false for a strict one.</summary>
    public bool IsOpen { get; private init; }

    /// <summary>
    /// Registers <typeparamref name="T"/> as subject <paramref name="name"/>, with the member paths conditions may use
    /// and the fields field-level rules may name. Paths are checked now, so a typo fails at startup.
    /// </summary>
    public SubjectRegistry Add<T>(string name, Action<SubjectBuilder<T>>? configure = null)
    {
        if (name == AllSubjects)
        {
            throw new CaslConfigurationException($"'{AllSubjects}' is reserved.");
        }

        SubjectBuilder<T> builder = new();
        configure?.Invoke(builder);
        SubjectDefinition definition = new(name, typeof(T), builder.ConditionPaths, builder.FieldNames, allowAnyPath: IsOpen && builder.ConditionPaths.Count == 0);
        _byName.Add(name, definition);
        _byType.Add(typeof(T), definition);
        return this;
    }

    /// <summary>The registered subjects.</summary>
    public IEnumerable<SubjectDefinition> Subjects => _byName.Values;

    /// <summary>The registered subject with this name; throws <see cref="CaslConfigurationException"/> when there is none.</summary>
    public SubjectDefinition Get(string name) =>
        _byName.TryGetValue(name, out SubjectDefinition? d) ? d : throw new CaslConfigurationException($"Unknown subject '{name}'.");

    /// <summary>The registered subject with this name, if there is one.</summary>
    public bool TryGet(string name, out SubjectDefinition? definition) => _byName.TryGetValue(name, out definition);

    /// <summary>The definition for a CLR type: the registered one, or in an open registry one made on the fly.</summary>
    public SubjectDefinition Get(Type type)
    {
        for (Type? t = type; t is not null; t = t.BaseType)
        {
            if (_byType.TryGetValue(t, out SubjectDefinition? registered))
            {
                return registered; // EF Core proxies derive from the entity type
            }
        }

        if (!IsOpen)
        {
            throw new CaslConfigurationException($"Type '{type.Name}' is not a registered subject.");
        }

        lock (_gate)
        {
            if (!_openByType.TryGetValue(type, out SubjectDefinition? open))
            {
                open = new SubjectDefinition(type.Name, type, [], [], allowAnyPath: true);
                _openByType[type] = open;
            }

            return open;
        }
    }

    /// <summary>The definition to compile a rule on <paramref name="subjectName"/> against an object of <paramref name="type"/>.</summary>
    internal SubjectDefinition For(string subjectName, Type type)
    {
        if (_byName.TryGetValue(subjectName, out SubjectDefinition? named) && named.Type.IsAssignableFrom(type))
        {
            return named;
        }

        return Get(type);
    }

    /// <summary>Whether rules may name this subject: any name in an open registry, registered names and <c>all</c> in a strict one.</summary>
    public bool IsKnown(string name) => IsOpen || name == AllSubjects || _byName.ContainsKey(name);
}

/// <summary>A subject: its name, its CLR type, and what rules may use from it.</summary>
public sealed class SubjectDefinition
{
    internal SubjectDefinition(string name, Type type, IReadOnlyCollection<string> conditionPaths, IReadOnlyCollection<string> fields, bool allowAnyPath)
    {
        Name = name;
        Type = type;
        AllowAnyPath = allowAnyPath;
        ConditionPaths = new HashSet<string>(conditionPaths, StringComparer.Ordinal);
        Fields = fields.ToList();
        foreach (string path in ConditionPaths)
        {
            _ = MemberPath.Resolve(type, path); // fail at registration, not at request time
        }
    }

    /// <summary>The subject type name rules use, such as <c>"WorkItem"</c>.</summary>
    public string Name { get; }

    /// <summary>The CLR type objects of this subject have.</summary>
    public Type Type { get; }

    /// <summary>True in an open registry: conditions may use any public member.</summary>
    public bool AllowAnyPath { get; }

    /// <summary>Member paths (camelCase, dotted for navigations) allowed in conditions.</summary>
    public IReadOnlySet<string> ConditionPaths { get; }

    /// <summary>Fields that field-level rules may name; also the "all fields" set for permitted-field checks.</summary>
    public IReadOnlyList<string> Fields { get; }

    internal bool Allows(string path) => AllowAnyPath || ConditionPaths.Contains(path);

    /// <summary>True when rules may only name the fields listed with <see cref="SubjectBuilder{T}.Fields"/>.</summary>
    internal bool ListsFields => !AllowAnyPath || Fields.Count > 0;

    /// <summary>Whether <paramref name="field"/> is a listed field or one under a listed family ("fields.**").</summary>
    internal bool HasField(string field) =>
        Fields.Contains(field) || Fields.Any(f => f.EndsWith(".**", StringComparison.Ordinal) && field.StartsWith(f[..^2], StringComparison.Ordinal));

    /// <summary>Whether conditions may use <paramref name="path"/> or a path under it ("comments" for "comments.authorId").</summary>
    internal bool AllowsUnder(string path) =>
        Allows(path) || ConditionPaths.Any(p => p.Length > path.Length && p[path.Length] == '.' && p.StartsWith(path, StringComparison.Ordinal));
}

/// <summary>Configures one subject in a registry: see <see cref="SubjectRegistry.Add{T}"/>.</summary>
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

    /// <summary>
    /// Fields that field-level rules can grant or deny, e.g. "summary", "status". A family ending in ".**", such as
    /// "fields.**", accepts every field under it ("fields.custom.cost"), for fields declared at run time.
    /// </summary>
    public SubjectBuilder<T> Fields(params string[] fields)
    {
        FieldNames.AddRange(fields);
        return this;
    }
}

/// <summary>One step of a member path: a member, or a numeric index into a collection ("authors.0").</summary>
internal readonly record struct PathSegment(string Name, MemberInfo? Member, int? Index, Type Type);

/// <summary>Resolves dotted camelCase paths ("project.orgUnitId", "comments.authorId", "authors.0") to CLR members.</summary>
internal static class MemberPath
{
    public static IReadOnlyList<PathSegment> Resolve(Type root, string path)
    {
        List<PathSegment> segments = [];
        Type current = root;
        foreach (string name in path.Split('.'))
        {
            if (int.TryParse(name, out int index) && index >= 0 && ExpressionBuilder.ElementTypeOf(current) is { } element)
            {
                segments.Add(new PathSegment(name, null, index, element));
                current = element;
                continue;
            }

            Type lookup = ExpressionBuilder.ElementTypeOf(current) ?? current; // "comments.authorId" steps into the element
            MemberInfo member = (MemberInfo?)lookup.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                ?? lookup.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)
                ?? throw new CaslConfigurationException($"'{root.Name}' has no member '{path}' (segment '{name}').");
            current = TypeOf(member);
            segments.Add(new PathSegment(name, member, null, current));
        }

        return segments;
    }

    /// <summary>
    /// For a path that passes through a collection ("comments.authorId"), the collection part and the rest
    /// ("comments", "authorId"). MongoDB reads such a path as "any element matches". Null when there is none.
    /// A numeric segment ("authors.0") is an index, not a pass-through.
    /// </summary>
    public static (string Collection, string Remainder)? SplitAtCollection(Type root, string path)
    {
        IReadOnlyList<PathSegment> segments = Resolve(root, path);
        for (int i = 0; i < segments.Count - 1; i++)
        {
            if (ExpressionBuilder.ElementTypeOf(segments[i].Type) is not null && segments[i + 1].Index is null)
            {
                return (string.Join('.', segments.Take(i + 1).Select(s => s.Name)), string.Join('.', segments.Skip(i + 1).Select(s => s.Name)));
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
