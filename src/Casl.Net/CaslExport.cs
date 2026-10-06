using System.Text.Json.Nodes;

namespace Casl;

/// <summary>
/// Adjusts conditions so @casl/ability in the browser reaches the same answers as this library, without
/// changing their MongoDB meaning:
/// - CASL treats a missing or null field as smaller than any value, so <c>{ dueAt: { $lt: X } }</c> would match
///   items with no due date. MongoDB, SQL and .NET don't. Ordering operators get <c>$ne: null</c> alongside.
/// - CASL's <c>$in: [null]</c> doesn't match a missing field; MongoDB's does. Null moves into an <c>$or</c>.
/// The browser also needs CASL's logical operators switched on; the <c>casl-net</c> package does that.
/// </summary>
internal static class CaslExport
{
    private static readonly string[] Ordering = ["$lt", "$lte", "$gt", "$gte"];

    public static RawRule Normalize(RawRule rule) =>
        rule.Conditions is null ? rule : rule with { Conditions = NormalizeQuery(rule.Conditions) };

    private static JsonObject NormalizeQuery(JsonObject query)
    {
        JsonObject result = [];
        List<JsonNode> extra = [];
        foreach ((string key, JsonNode? value) in query)
        {
            if (key is "$and" or "$or" or "$nor" && value is JsonArray array)
            {
                result[key] = new JsonArray(array.Select(n => (JsonNode?)(n is JsonObject o ? NormalizeQuery(o) : n?.DeepClone())).ToArray());
            }
            else if (value is JsonObject operators && operators.All(p => p.Key.StartsWith('$')))
            {
                (JsonObject normalized, JsonNode? alternative) = NormalizeOperators(key, operators);
                if (alternative is null)
                {
                    result[key] = normalized;
                }
                else
                {
                    extra.Add(alternative);
                    if (normalized.Count > 0)
                    {
                        extra.Add(new JsonObject { [key] = normalized });
                    }
                }
            }
            else
            {
                result[key] = value?.DeepClone();
            }
        }

        if (extra.Count == 0)
        {
            return result;
        }

        // Rewritten parts are combined with $and, keeping the rest of the query as it was.
        JsonArray all = [];
        if (result.Count > 0)
        {
            all.Add(result);
        }

        foreach (JsonNode node in extra)
        {
            all.Add(node);
        }

        return all.Count == 1 ? (JsonObject)all[0]!.DeepClone() : new JsonObject { ["$and"] = all };
    }

    private static (JsonObject Operators, JsonNode? Alternative) NormalizeOperators(string field, JsonObject operators)
    {
        JsonObject result = [];
        JsonNode? alternative = null;
        foreach ((string op, JsonNode? value) in operators)
        {
            switch (op)
            {
                case "$elemMatch" when value is JsonObject inner:
                    result[op] = inner.All(p => p.Key.StartsWith('$')) ? NormalizeOperators(string.Empty, inner).Operators : NormalizeQuery(inner);
                    break;
                case "$not" when value is JsonObject inner:
                    result[op] = NormalizeOperators(field, inner).Operators;
                    break;
                case "$in" when value is JsonArray values && values.Any(v => v is null):
                    // { f: { $in: [a, null] } }  ->  { $or: [ { f: { $in: [a] } }, { f: null } ] }
                    JsonArray nonNull = new(values.Where(v => v is not null).Select(v => v!.DeepClone()).ToArray());
                    alternative = new JsonObject
                    {
                        ["$or"] = new JsonArray(
                            new JsonObject { [field] = new JsonObject { ["$in"] = nonNull } },
                            new JsonObject { [field] = null }),
                    };
                    break;
                default:
                    result[op] = value?.DeepClone();
                    break;
            }
        }

        if (operators.Any(p => Ordering.Contains(p.Key)) && !result.ContainsKey("$ne"))
        {
            result["$ne"] = null;
        }
        else if (operators.Any(p => Ordering.Contains(p.Key)))
        {
            // $ne is already taken; guard with $exists instead (CASL reads null as missing too).
            alternative ??= new JsonObject { [field] = new JsonObject { ["$exists"] = true, ["$ne"] = null } };
        }

        return (result, alternative);
    }
}
