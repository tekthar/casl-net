// Ported from CASL's spec/rulesToCondition.spec.ts (https://github.com/stalniy/casl, packages/casl-ability, commit e2b885a),
// MIT License, Copyright (c) 2017-present Sergii Stotskyi.

using System.Text.Json.Nodes;
using static Casl.Tests.CaslSpec.Spec;

namespace Casl.Tests.CaslSpec;

public class RulesToConditionSpec
{
    private static string? ToQuery(Ability ability, string action, string subject) =>
        Ability.RulesToCondition<JsonNode>(
            ability.RulesFor(action, subject),
            rule => rule.Inverted ? new JsonObject { ["$not"] = rule.Conditions!.DeepClone() } : rule.Conditions!.DeepClone(),
            and => new JsonObject { ["$and"] = new JsonArray(and.Select(n => (JsonNode?)n.DeepClone()).ToArray()) },
            or => new JsonObject { ["$or"] = new JsonArray(or.Select(n => (JsonNode?)n.DeepClone()).ToArray()) },
            () => new JsonObject())?.ToJsonString();

    [Fact]
    public void Returns_empty_object_if_there_are_no_rules_with_conditions() =>
        Assert.Equal("{}", ToQuery(Define(b => b.Can("read", "Post")), "read", "Post"));

    [Fact]
    public void Returns_empty_object_if_at_least_one_regular_rule_does_not_have_conditions()
    {
        Ability ability = Define(b =>
        {
            b.Can("read", "Post", new { author = 123 });
            b.Can("read", "Post");
        });

        Assert.Equal("{}", ToQuery(ability, "read", "Post"));
    }

    [Fact]
    public void Returns_empty_object_if_rule_with_conditions_defined_last()
    {
        Ability ability = Define(b =>
        {
            b.Can("read", "Post");
            b.Can("read", "Post", new { author = 123 });
        });

        Assert.Equal("{}", ToQuery(ability, "read", "Post"));
    }

    [Fact]
    public void Returns_null_if_empty_ability_is_passed() =>
        Assert.Null(ToQuery(new AbilityBuilder().Build(), "read", "Post"));

    [Fact]
    public void Returns_null_if_specified_only_inverted_rules() =>
        Assert.Null(ToQuery(Define(b => b.Cannot("read", "Post", new { @private = true })), "read", "Post"));

    [Fact]
    public void Returns_null_if_inverted_rule_does_not_have_conditions_and_there_are_no_direct_rules()
    {
        Ability ability = Define(b =>
        {
            b.Cannot("read", "Post", new { author = 123 });
            b.Cannot("read", "Post");
        });

        Assert.Null(ToQuery(ability, "read", "Post"));
    }

    [Fact]
    public void Returns_null_if_at_least_one_inverted_rule_does_not_have_conditions_even_if_direct_rule_exists()
    {
        Ability ability = Define(b =>
        {
            b.Can("read", "Post", new { @public = true });
            b.Cannot("read", "Post", new { author = 321 });
            b.Cannot("read", "Post");
        });

        Assert.Null(ToQuery(ability, "read", "Post"));
    }

    [Fact]
    public void Returns_query_if_there_is_a_regular_rule_after_last_unconditional_inverted_one()
    {
        Ability ability = Define(b =>
        {
            b.Can("read", "Post", new { @public = true });
            b.Cannot("read", "Post", new { author = 321 });
            b.Cannot("read", "Post");
            b.Can("read", "Post", new { author = 123 });
        });

        Assert.Equal("""{"$or":[{"author":123}]}""", ToQuery(ability, "read", "Post"));
    }

    [Fact]
    public void Keeps_higher_priority_regular_rules_when_lower_priority_inverted_rule_is_unconditional()
    {
        Ability ability = Define(b =>
        {
            b.Cannot("read", "Post");
            b.Can("read", "Post", new { author = 123 });
        });

        Assert.Equal("""{"$or":[{"author":123}]}""", ToQuery(ability, "read", "Post"));
    }

    [Fact]
    public void Ors_conditions_for_regular_rules()
    {
        Ability ability = Define(b =>
        {
            b.Can("read", "Post", new { status = "draft", createdBy = "someoneelse" });
            b.Can("read", "Post", new { status = "published", createdBy = "me" });
        });

        Assert.Equal(
            """{"$or":[{"status":"published","createdBy":"me"},{"status":"draft","createdBy":"someoneelse"}]}""",
            ToQuery(ability, "read", "Post"));
    }

    [Fact]
    public void Ands_conditions_for_inverted_rules()
    {
        Ability ability = Define(b =>
        {
            b.Can("read", "Post");
            b.Cannot("read", "Post", new { status = "draft", createdBy = "someoneelse" });
            b.Cannot("read", "Post", new { status = "published", createdBy = "me" });
        });

        Assert.Equal(
            """{"$and":[{"$not":{"status":"published","createdBy":"me"}},{"$not":{"status":"draft","createdBy":"someoneelse"}}]}""",
            ToQuery(ability, "read", "Post"));
    }

    [Fact]
    public void Ors_conditions_for_regular_rules_and_ands_for_inverted_ones()
    {
        Ability ability = Define(b =>
        {
            b.Can("read", "Post", new { _id = "mega" });
            b.Can("read", "Post", new { state = "draft" });
            b.Cannot("read", "Post", new { @private = true });
            b.Cannot("read", "Post", new { state = "archived" });
        });

        Assert.Equal(
            """{"$or":[{"$and":[{"state":"draft"},{"$not":{"state":"archived"}},{"$not":{"private":true}}]},""" +
            """{"$and":[{"_id":"mega"},{"$not":{"state":"archived"}},{"$not":{"private":true}}]}]}""",
            ToQuery(ability, "read", "Post"));
    }

    [Fact]
    public void Builds_query_for_a_complex_mix_of_priority_ordered_regular_and_inverted_rules()
    {
        Ability ability = Define(b =>
        {
            b.Can("read", "Post", new { ownerId = 1 });
            b.Cannot("read", "Post", new { archived = true });
            b.Can("read", "Post", new { shared = true });
            b.Cannot("read", "Post", new { hidden = true });
            b.Can("read", "Post", new { @public = true });
            b.Cannot("read", "Post", new { groupped = true });
            b.Cannot("read", "Post", new { flagged = true });
            b.Can("read", "Post", new { status = "pinned" });
        });

        Assert.Equal(
            """{"$or":[{"status":"pinned"},""" +
            """{"$and":[{"public":true},{"$not":{"flagged":true}},{"$not":{"groupped":true}}]},""" +
            """{"$and":[{"shared":true},{"$not":{"flagged":true}},{"$not":{"groupped":true}},{"$not":{"hidden":true}}]},""" +
            """{"$and":[{"ownerId":1},{"$not":{"flagged":true}},{"$not":{"groupped":true}},{"$not":{"hidden":true}},{"$not":{"archived":true}}]}]}""",
            ToQuery(ability, "read", "Post"));
    }

    [Fact]
    public void Ignores_inverted_rules_that_are_overridden_by_higher_priority_regular_rules()
    {
        Ability ability = Define(b =>
        {
            b.Cannot("manage", "Post", new { id = 1 });
            b.Can("manage", "Post", new { id = 1 });
        });

        Assert.Equal("""{"$or":[{"id":1}]}""", ToQuery(ability, "manage", "Post"));
    }

    [Fact]
    public void Returns_empty_query_if_inverted_rule_with_conditions_defined_before_regular_rule_without_conditions()
    {
        Ability ability = Define(b =>
        {
            b.Can("read", "Post", new { author = 123 });
            b.Cannot("read", "Post", new { @private = true });
            b.Can("read", "Post");
        });

        Assert.Equal("{}", ToQuery(ability, "read", "Post"));
    }

    [Fact]
    public void Adds_a_fallback_branch_bounded_by_higher_priority_inverted_rules_for_lower_priority_unconditional_rule()
    {
        Ability ability = Define(b =>
        {
            b.Can("read", "Post");
            b.Cannot("read", "Post", new { @private = true });
            b.Can("read", "Post", new { author = 123 });
        });

        Assert.Equal("""{"$or":[{"author":123},{"$and":[{"$not":{"private":true}}]}]}""", ToQuery(ability, "read", "Post"));
    }

    [Fact]
    public void Ignores_inverted_rules_with_fields_and_conditions()
    {
        Ability ability = Define(b =>
        {
            b.Can("read", "Post", new { author = 123 });
            b.Cannot("read", "Post", new { @private = true }, null, "description");
        });

        Assert.Equal("""{"$or":[{"author":123}]}""", ToQuery(ability, "read", "Post"));
    }

    [Fact]
    public void Ignores_inverted_rules_with_fields_and_without_conditions()
    {
        Ability ability = Define(b =>
        {
            b.Can("read", "Post", new { author = 123 });
            b.Cannot("read", "Post").OnFields("description");
        });

        Assert.Equal("""{"$or":[{"author":123}]}""", ToQuery(ability, "read", "Post"));
    }
}
