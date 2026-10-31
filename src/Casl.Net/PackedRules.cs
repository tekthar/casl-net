// The packed format is CASL's packRules/unpackRules
// (https://github.com/stalniy/casl, packages/casl-ability/src/extra/packRules.ts),
// MIT License, Copyright (c) 2017-present Sergii Stotskyi.

using System.Text.Json.Nodes;

namespace Casl;

/// <summary>
/// CASL's compact rule format for sending rules over the wire:
/// <c>[actions, subjects, conditions | 0, inverted ? 1 : 0, fields | 0, reason]</c>, with lists joined by commas and
/// trailing empty entries dropped. Compatible with <c>packRules</c>/<c>unpackRules</c> in @casl/ability.
/// </summary>
public static class PackedRules
{
    /// <summary>Packs raw rules, e.g. <see cref="Ability.ToCaslRules"/>, for <c>unpackRules</c> in the browser.</summary>
    public static JsonArray Pack(IEnumerable<RawRule> rules) =>
        new(rules.Select(rule => (JsonNode?)PackOne(rule)).ToArray());

    /// <summary>Packs an ability's rules, with placeholders filled in.</summary>
    public static JsonArray Pack(IEnumerable<Rule> rules) => Pack(rules.Select(r => r.Origin));

    /// <summary>
    /// Unpacks rules packed here or by CASL's <c>packRules</c>. Throws <see cref="CaslConfigurationException"/> for an
    /// element of the wrong type, such as conditions that aren't an object or an inverted flag other than 0 or 1,
    /// rather than reading it as absent, which would widen the rule.
    /// </summary>
    public static IReadOnlyList<RawRule> Unpack(JsonArray packed) =>
        packed.Select(item => UnpackOne(item as JsonArray ?? throw new CaslConfigurationException("Each packed rule must be an array."))).ToList();

    /// <summary>Unpacks rules from their JSON text.</summary>
    public static IReadOnlyList<RawRule> Unpack(string json) =>
        Unpack(JsonNode.Parse(json) as JsonArray ?? throw new CaslConfigurationException("Packed rules must be a JSON array."));

    private static JsonArray PackOne(RawRule rule)
    {
        List<JsonNode?> packed =
        [
            string.Join(',', rule.Action),
            rule.Subject is { Count: > 0 } subjects ? string.Join(',', subjects) : string.Empty,
            rule.Conditions is { } conditions ? conditions.DeepClone() : 0,
            rule.Inverted ? 1 : 0,
            rule.Fields is { } fields ? string.Join(',', fields) : 0,
            rule.Reason ?? string.Empty,
        ];

        while (packed.Count > 0 && IsEmpty(packed[^1]))
        {
            packed.RemoveAt(packed.Count - 1);
        }

        return new JsonArray(packed.ToArray());
    }

    // Accepts exactly the shapes packRules writes and throws on anything else, as RawRule.Parse does: reading an
    // unexpected value as "absent" would widen the rule (a deny read as an allow, conditions or fields dropped).
    private static RawRule UnpackOne(JsonArray packed)
    {
        if (packed.Count is 0 or > 6)
        {
            throw new CaslConfigurationException("A packed rule has 1 to 6 elements: [actions, subjects, conditions, inverted, fields, reason].");
        }

        JsonNode? At(int i) => i < packed.Count ? packed[i] : null;

        // packRules writes 0 for absent conditions and fields; a missing trailing element or null means the same.
        static bool IsAbsent(JsonNode? node) => node is null || (node is JsonValue v && v.TryGetValue(out int n) && n == 0);

        string? Text(int i, string name, bool zeroAllowed) => At(i) switch
        {
            null => null,
            JsonValue v when v.TryGetValue(out string? s) => s,
            JsonNode n when zeroAllowed && IsAbsent(n) => null,
            _ => throw new CaslConfigurationException($"A packed rule's {name} must be a string."),
        };

        string action = Text(0, "actions", zeroAllowed: false) is { Length: > 0 } a
            ? a
            : throw new CaslConfigurationException("A packed rule needs its actions.");
        string? subject = Text(1, "subjects", zeroAllowed: false);
        string? fields = Text(4, "fields", zeroAllowed: true);
        string? reason = Text(5, "reason", zeroAllowed: false);

        return new RawRule
        {
            Action = action.Split(','),
            Subject = string.IsNullOrEmpty(subject) ? null : subject.Split(','),
            Conditions = At(2) switch
            {
                JsonObject conditions => (JsonObject)conditions.DeepClone(),
                JsonNode n when !IsAbsent(n) => throw new CaslConfigurationException("A packed rule's conditions must be an object, or 0 for none."),
                _ => null,
            },
            Inverted = At(3) switch
            {
                null => false,
                JsonValue v when v.TryGetValue(out bool b) => b,
                JsonValue v when v.TryGetValue(out int flag) && flag is 0 or 1 => flag == 1,
                _ => throw new CaslConfigurationException("A packed rule's inverted flag must be 0 or 1."),
            },
            Fields = string.IsNullOrEmpty(fields) ? null : fields.Split(','),
            Reason = string.IsNullOrEmpty(reason) ? null : reason,
        };
    }

    private static bool IsEmpty(JsonNode? node) => node switch
    {
        null => true,
        JsonValue v when v.TryGetValue(out int n) => n == 0,
        JsonValue v when v.TryGetValue(out string? s) => string.IsNullOrEmpty(s),
        _ => false,
    };
}
