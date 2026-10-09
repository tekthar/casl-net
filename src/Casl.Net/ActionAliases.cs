// The expansion and validation logic follows CASL's createAliasResolver
// (https://github.com/stalniy/casl, packages/casl-ability/src/utils.ts),
// MIT License, Copyright (c) 2017-present Sergii Stotskyi.

namespace Casl;

/// <summary>
/// Action aliases: a rule for <c>modify</c> also covers <c>update</c> and <c>delete</c> when
/// <c>modify</c> = [<c>update</c>, <c>delete</c>]. Aliases can nest; cycles and aliases involving the
/// any-action (<c>manage</c>) are rejected.
/// </summary>
public sealed class ActionAliases
{
    private readonly Dictionary<string, string[]> _map;

    private ActionAliases(Dictionary<string, string[]> map) => _map = map;

    /// <summary>
    /// Aliases from a map of alias to actions, e.g. <c>["modify"] = ["update", "delete"]</c>. With
    /// <paramref name="validate"/>, cycles and aliases of <paramref name="anyAction"/> throw
    /// <see cref="CaslConfigurationException"/>.
    /// </summary>
    public static ActionAliases Create(IReadOnlyDictionary<string, string[]> aliases, string anyAction = SubjectRegistry.AllActions, bool validate = true)
    {
        Dictionary<string, string[]> map = aliases.ToDictionary(p => p.Key, p => p.Value.ToArray(), StringComparer.Ordinal);
        if (validate)
        {
            Validate(map, anyAction);
        }

        return new ActionAliases(map);
    }

    /// <summary>Aliases from a map of alias to one action each, e.g. <c>["edit"] = "update"</c>.</summary>
    public static ActionAliases Create(IReadOnlyDictionary<string, string> aliases, string anyAction = SubjectRegistry.AllActions, bool validate = true) =>
        Create(aliases.ToDictionary(p => p.Key, p => new[] { p.Value }), anyAction, validate);

    /// <summary>The actions themselves plus everything they alias, recursively, in CASL's order.</summary>
    public IReadOnlyList<string> Expand(IEnumerable<string> actions) => Expand(_map, actions, (list, more) => list.AddRange(more));

    private static List<string> Expand(Dictionary<string, string[]> map, IEnumerable<string> raw, Action<List<string>, string[]> merge)
    {
        List<string> actions = [.. raw];
        for (int i = 0; i < actions.Count; i++)
        {
            if (map.TryGetValue(actions[i], out string[]? aliased))
            {
                merge(actions, aliased);
            }
        }

        return actions;
    }

    private static void Validate(Dictionary<string, string[]> map, string reservedAction)
    {
        if (map.ContainsKey(reservedAction))
        {
            throw new CaslConfigurationException($"Cannot use \"{reservedAction}\" as an alias because it's reserved action.");
        }

        foreach (string key in map.Keys)
        {
            Expand(map, [key], (actions, more) =>
            {
                string? duplicate = more.FirstOrDefault(actions.Contains);
                if (duplicate is not null)
                {
                    throw new CaslConfigurationException($"Detected cycle {duplicate} -> {string.Join(", ", actions)}");
                }

                if (more.Contains(reservedAction) || actions.Contains(reservedAction))
                {
                    throw new CaslConfigurationException($"Cannot make an alias to \"{reservedAction}\" because this is reserved action");
                }

                actions.AddRange(more);
            });
        }
    }
}
