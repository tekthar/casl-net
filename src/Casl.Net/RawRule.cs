using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Casl;

/// <summary>
/// A rule in CASL's raw format: <c>{ action, subject, conditions, fields, inverted, reason }</c>.
/// <c>action</c>, <c>subject</c> and <c>fields</c> accept a string or an array of strings, as in CASL.
/// Conditions use MongoDB query syntax and may contain placeholders such as <c>"${user.id}"</c>.
/// </summary>
[JsonConverter(typeof(RawRuleJsonConverter))]
public sealed record RawRule
{
    /// <summary>One or more actions. <c>manage</c> means every action.</summary>
    public required IReadOnlyList<string> Action { get; init; }

    /// <summary>One or more subject names. <c>all</c> means every subject; no subject makes a claim rule (subject <c>all</c>).</summary>
    public IReadOnlyList<string>? Subject { get; init; }

    /// <summary>MongoDB-style query; null means the rule applies to every instance.</summary>
    public JsonObject? Conditions { get; init; }

    /// <summary>Fields the rule is limited to; null means all fields.</summary>
    public IReadOnlyList<string>? Fields { get; init; }

    /// <summary>True for a "cannot" rule.</summary>
    public bool Inverted { get; init; }

    /// <summary>Shown to the user when this rule denies access.</summary>
    public string? Reason { get; init; }

    public static RawRule Can(string action, string? subject = null, object? conditions = null, IReadOnlyList<string>? fields = null) =>
        new() { Action = [action], Subject = subject is null ? null : [subject], Conditions = ToConditions(conditions), Fields = fields };

    public static RawRule Cannot(string action, string? subject = null, object? conditions = null, IReadOnlyList<string>? fields = null, string? reason = null) =>
        new() { Action = [action], Subject = subject is null ? null : [subject], Conditions = ToConditions(conditions), Fields = fields, Inverted = true, Reason = reason };

    /// <summary>Parses a JSON array of raw rules.</summary>
    public static IReadOnlyList<RawRule> ParseMany(string json) =>
        JsonSerializer.Deserialize<List<RawRule>>(json) ?? [];

    public static RawRule Parse(string json) =>
        JsonSerializer.Deserialize<RawRule>(json) ?? throw new CaslConfigurationException("Empty rule.");

    public string ToJson() => JsonSerializer.Serialize(this);

    internal static JsonObject? ToConditions(object? conditions) => conditions switch
    {
        null => null,
        JsonObject o => o,
        string s => JsonNode.Parse(s)?.AsObject(),
        _ => JsonSerializer.SerializeToNode(conditions)?.AsObject(),
    };
}

internal sealed class RawRuleJsonConverter : JsonConverter<RawRule>
{
    public override RawRule Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        JsonObject obj = JsonNode.Parse(ref reader)?.AsObject() ?? throw new JsonException("A rule must be a JSON object.");
        return new RawRule
        {
            Action = StringOrArray(obj["action"], "action") ?? throw new JsonException("A rule needs an 'action'."),
            Subject = StringOrArray(obj["subject"], "subject"),
            Conditions = obj["conditions"] is JsonObject c ? (JsonObject)c.DeepClone() : null,
            Fields = StringOrArray(obj["fields"], "fields"),
            Inverted = obj["inverted"]?.GetValue<bool>() ?? false,
            Reason = obj["reason"]?.GetValue<string>(),
        };
    }

    public override void Write(Utf8JsonWriter writer, RawRule value, JsonSerializerOptions options)
    {
        JsonObject obj = new()
        {
            ["action"] = value.Action.Count == 1 ? value.Action[0] : new JsonArray(value.Action.Select(a => (JsonNode?)a).ToArray()),
        };
        if (value.Subject is { Count: > 0 } subjects)
        {
            obj["subject"] = subjects.Count == 1 ? subjects[0] : new JsonArray(subjects.Select(s => (JsonNode?)s).ToArray());
        }
        if (value.Conditions is not null)
        {
            obj["conditions"] = value.Conditions.DeepClone();
        }

        if (value.Fields is not null)
        {
            obj["fields"] = new JsonArray(value.Fields.Select(f => (JsonNode?)f).ToArray());
        }

        if (value.Inverted)
        {
            obj["inverted"] = true;
        }

        if (value.Reason is not null)
        {
            obj["reason"] = value.Reason;
        }

        obj.WriteTo(writer);
    }

    private static IReadOnlyList<string>? StringOrArray(JsonNode? node, string name) => node switch
    {
        null => null,
        JsonValue v when v.TryGetValue(out string? s) => [s],
        JsonArray a => a.Select(x => x?.GetValue<string>() ?? throw new JsonException($"'{name}' entries must be strings.")).ToList(),
        _ => throw new JsonException($"'{name}' must be a string or an array of strings."),
    };
}

/// <summary>Thrown when rules or subjects are configured in a way that can't be enforced safely.</summary>
public sealed class CaslConfigurationException(string message) : Exception(message);
