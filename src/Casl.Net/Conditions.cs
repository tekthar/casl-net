using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Casl;

// The condition tree a MongoDB-style query parses into. One tree feeds both the in-memory check
// and the LINQ expression EF Core translates to SQL.

internal abstract record Condition;

internal sealed record AndCondition(IReadOnlyList<Condition> Items) : Condition;

internal sealed record OrCondition(IReadOnlyList<Condition> Items) : Condition;

internal sealed record NotCondition(Condition Inner) : Condition;

/// <summary>An operator on one member. An empty path means "the element itself" inside $elemMatch.</summary>
internal sealed record FieldCondition(string Path, string Operator, JsonNode? Value) : Condition;

/// <summary>At least one element of a collection member matches the inner condition.</summary>
internal sealed record ElemMatchCondition(string Path, Condition Inner) : Condition;

/// <summary>Parses the supported subset of MongoDB query syntax, the same subset CASL's mongo matcher covers.</summary>
internal static class ConditionParser
{
    private static readonly HashSet<string> ComparisonOperators = ["$eq", "$ne", "$gt", "$gte", "$lt", "$lte", "$in", "$nin", "$exists"];

    public static Condition Parse(JsonObject query) => ParseQuery(query);

    private static Condition ParseQuery(JsonObject query)
    {
        List<Condition> items = [];
        foreach ((string key, JsonNode? value) in query)
        {
            switch (key)
            {
                case "$and":
                    items.Add(new AndCondition(SubQueries(key, value)));
                    break;
                case "$or":
                    items.Add(new OrCondition(SubQueries(key, value)));
                    break;
                case "$nor":
                    items.Add(new NotCondition(new OrCondition(SubQueries(key, value))));
                    break;
                case var op when op.StartsWith('$'):
                    throw new CaslConfigurationException($"Operator '{op}' is not supported at the top of a query.");
                default:
                    items.Add(ParseField(key, value));
                    break;
            }
        }

        return items.Count == 1 ? items[0] : new AndCondition(items);
    }

    private static List<Condition> SubQueries(string op, JsonNode? value) =>
        value is JsonArray array && array.Count > 0
            ? array.Select(n => n is JsonObject o ? ParseQuery(o) : throw new CaslConfigurationException($"'{op}' needs an array of query objects.")).ToList()
            : throw new CaslConfigurationException($"'{op}' needs a non-empty array.");

    private static Condition ParseField(string path, JsonNode? value)
    {
        if (value is JsonObject obj && obj.Count > 0 && obj.All(p => p.Key.StartsWith('$')))
        {
            return ParseOperators(path, obj);
        }

        return value switch
        {
            JsonObject => throw new CaslConfigurationException($"Matching '{path}' against a whole object isn't supported; use dotted paths such as '{path}.id'."),
            JsonArray => throw new CaslConfigurationException($"Matching '{path}' against an exact array isn't supported; use $in or $all."),
            _ => new FieldCondition(path, "$eq", value?.DeepClone()),
        };
    }

    private static Condition ParseOperators(string path, JsonObject operators)
    {
        List<Condition> items = [];
        foreach ((string op, JsonNode? value) in operators)
        {
            switch (op)
            {
                case "$in" or "$nin" when value is not JsonArray && !Placeholders.IsWholePlaceholder(value):
                    throw new CaslConfigurationException($"'{op}' on '{path}' needs an array.");
                case "$exists" when value is not JsonValue v || !v.TryGetValue(out bool _):
                    throw new CaslConfigurationException($"'$exists' on '{path}' needs true or false.");
                case var c when ComparisonOperators.Contains(c):
                    items.Add(new FieldCondition(path, op, value?.DeepClone()));
                    break;
                case "$not" when value is JsonObject inner && inner.All(p => p.Key.StartsWith('$')):
                    items.Add(new NotCondition(ParseOperators(path, inner)));
                    break;
                case "$all" when value is JsonArray all:
                    // "contains every value": one equality per value on the collection
                    items.Add(new AndCondition(all.Select(v => (Condition)new FieldCondition(path, "$eq", v?.DeepClone())).ToList()));
                    break;
                case "$elemMatch" when value is JsonObject inner:
                    items.Add(new ElemMatchCondition(path, inner.All(p => p.Key.StartsWith('$'))
                        ? ParseOperators(string.Empty, inner)
                        : ParseQuery(inner)));
                    break;
                default:
                    throw new CaslConfigurationException($"Operator '{op}' on '{path}' is not supported. Supported: $eq $ne $gt $gte $lt $lte $in $nin $exists $not $all $elemMatch $and $or $nor.");
            }
        }

        return items.Count == 1 ? items[0] : new AndCondition(items);
    }
}

/// <summary>
/// Replaces placeholders in conditions. A string that is exactly <c>"${user.groupIds}"</c> becomes the variable's
/// value, of any JSON type (an array, a number...). Placeholders inside a longer string are formatted as text.
/// A placeholder with no value fails the build: silently dropping it could widen or narrow access.
/// </summary>
internal static partial class Placeholders
{
    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_.]*)\}")]
    private static partial Regex PlaceholderPattern();

    public static bool IsWholePlaceholder(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out string? s) && PlaceholderPattern().Match(s) is { Success: true } m && m.Length == s.Length;

    public static JsonObject Interpolate(JsonObject conditions, Func<string, JsonNode?> resolve) =>
        (JsonObject)Interpolate((JsonNode)conditions, resolve)!;

    private static JsonNode? Interpolate(JsonNode? node, Func<string, JsonNode?> resolve)
    {
        switch (node)
        {
            case JsonObject obj:
                JsonObject copy = [];
                foreach ((string key, JsonNode? value) in obj)
                {
                    copy[key] = Interpolate(value, resolve);
                }

                return copy;
            case JsonArray array:
                return new JsonArray(array.Select(item => Interpolate(item, resolve)).ToArray());
            case JsonValue value when value.TryGetValue(out string? text) && text.Contains("${", StringComparison.Ordinal):
                Match whole = PlaceholderPattern().Match(text);
                if (whole.Success && whole.Length == text.Length)
                {
                    return resolve(whole.Groups[1].Value)?.DeepClone();
                }

                return JsonValue.Create(PlaceholderPattern().Replace(text, m => resolve(m.Groups[1].Value) switch
                {
                    JsonValue v when v.TryGetValue(out string? s) => s,
                    JsonNode n => n.ToJsonString(),
                    null => string.Empty,
                }));
            default:
                return node?.DeepClone();
        }
    }
}

/// <summary>Variables for placeholders, from anonymous objects or dictionaries, looked up by dotted path.</summary>
internal sealed class VariableScope
{
    private readonly List<JsonObject> _layers = [];

    public void Push(object variables)
    {
        JsonNode? node = variables as JsonNode ?? JsonSerializer.SerializeToNode(variables);
        if (node is not JsonObject obj)
        {
            throw new CaslConfigurationException("Variables must be an object, such as new { user = new { id } }.");
        }

        _layers.Insert(0, obj);
    }

    public VariableScope With(object? variables)
    {
        VariableScope scope = new();
        scope._layers.AddRange(_layers);
        if (variables is not null)
        {
            scope.Push(variables);
        }

        return scope;
    }

    public JsonNode? Resolve(string path)
    {
        foreach (JsonObject layer in _layers)
        {
            if (TryResolve(layer, path, out JsonNode? value))
            {
                return value;
            }
        }

        throw new CaslConfigurationException($"No value for placeholder '${{{path}}}'.");
    }

    private static bool TryResolve(JsonObject root, string path, out JsonNode? value)
    {
        JsonNode? current = root;
        foreach (string segment in path.Split('.'))
        {
            if (current is not JsonObject obj)
            {
                value = null;
                return false;
            }

            KeyValuePair<string, JsonNode?> match = obj.FirstOrDefault(p => string.Equals(p.Key, segment, StringComparison.OrdinalIgnoreCase));
            if (match.Key is null)
            {
                value = null;
                return false;
            }

            current = match.Value;
        }

        value = current;
        return true;
    }
}
