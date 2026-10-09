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

    /// <summary>Unpacks rules packed here or by CASL's <c>packRules</c>.</summary>
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

    private static RawRule UnpackOne(JsonArray packed)
    {
        string Text(int i) => i < packed.Count && packed[i] is JsonValue v && v.TryGetValue(out string? s) ? s : string.Empty;

        string subject = Text(1);
        return new RawRule
        {
            Action = Text(0).Split(','),
            Subject = subject.Length == 0 ? null : subject.Split(','),
            Conditions = packed.Count > 2 && packed[2] is JsonObject conditions ? (JsonObject)conditions.DeepClone() : null,
            Inverted = packed.Count > 3 && packed[3] is JsonValue inverted && inverted.TryGetValue(out int flag) && flag == 1,
            Fields = Text(4) is { Length: > 0 } fields ? fields.Split(',') : null,
            Reason = Text(5) is { Length: > 0 } reason ? reason : null,
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
