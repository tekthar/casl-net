// Ported from CASL's spec/pack_rules.spec.ts (https://github.com/stalniy/casl, packages/casl-ability, commit e2b885a),
// MIT License, Copyright (c) 2017-present Sergii Stotskyi.

using System.Text.Json.Nodes;

namespace Casl.Tests.CaslSpec;

public class PackRulesSpec
{
    private static JsonArray First(params RawRule[] rules) => (JsonArray)PackedRules.Pack(rules)[0]!;

    private static RawRule Unpack(string packed) => PackedRules.Unpack(packed)[0];

    [Fact]
    public void Converts_array_of_rule_objects_to_array_of_rule_arrays()
    {
        JsonArray packed = PackedRules.Pack([RawRule.Can("read", "Post"), RawRule.Can("delete", "Post")]);

        Assert.All(packed, rule => Assert.IsType<JsonArray>(rule));
    }

    [Fact]
    public void Puts_actions_as_1st_element()
    {
        JsonArray rule = First(RawRule.Can("read", "Post"));

        Assert.Equal("read", rule[0]!.GetValue<string>());
        Assert.Equal(2, rule.Count);
    }

    [Fact]
    public void Joins_actions_with_comma_if_its_value_is_an_array()
    {
        JsonArray rule = First(new RawRule { Action = ["read", "update"], Subject = ["Post"] });

        Assert.Equal("read,update", rule[0]!.GetValue<string>());
        Assert.Equal(2, rule.Count);
    }

    [Fact]
    public void Puts_subject_as_2nd_element()
    {
        JsonArray rule = First(RawRule.Can("read", "Post"));

        Assert.Equal("Post", rule[1]!.GetValue<string>());
        Assert.Equal(2, rule.Count);
    }

    [Fact]
    public void Joins_subject_with_comma_if_its_value_is_an_array()
    {
        JsonArray rule = First(new RawRule { Action = ["read"], Subject = ["Post", "Comment"] });

        Assert.Equal("Post,Comment", rule[1]!.GetValue<string>());
        Assert.Equal(2, rule.Count);
    }

    [Fact]
    public void Puts_conditions_as_3rd_element()
    {
        JsonArray rule = First(RawRule.Can("read", "Post", new { @private = true }));

        Assert.Equal("""{"private":true}""", rule[2]!.ToJsonString());
        Assert.Equal(3, rule.Count);
    }

    [Fact]
    public void Puts_0_in_place_of_conditions_if_they_are_not_defined_but_fields_are()
    {
        JsonArray rule = First(RawRule.Can("read", "Post", fields: ["title"]));

        Assert.Equal(0, rule[2]!.GetValue<int>());
        Assert.Equal(5, rule.Count);
    }

    [Fact]
    public void Converts_inverted_to_number_and_puts_it_as_4th_element()
    {
        JsonArray rule = First(RawRule.Cannot("read", "Post"));

        Assert.Equal(1, rule[3]!.GetValue<int>());
        Assert.Equal(4, rule.Count);
    }

    [Fact]
    public void Joins_fields_and_puts_it_as_5th_element()
    {
        JsonArray rule = First(RawRule.Can("read", "Post", fields: ["title", "description"]));

        Assert.Equal("title,description", rule[4]!.GetValue<string>());
        Assert.Equal(5, rule.Count);
    }

    [Fact]
    public void Puts_0_in_place_of_fields_when_reason_is_provided_and_fields_are_not()
    {
        JsonArray rule = First(new RawRule { Action = ["read"], Subject = ["Post"], Reason = "forbidden reason" });

        Assert.Equal(0, rule[4]!.GetValue<int>());
        Assert.Equal(6, rule.Count);
    }

    [Fact]
    public void Puts_reason_as_6th_element()
    {
        JsonArray rule = First(new RawRule { Action = ["read"], Subject = ["Post"], Reason = "forbidden reason" });

        Assert.Equal("forbidden reason", rule[5]!.GetValue<string>());
        Assert.Equal(6, rule.Count);
    }

    [Fact]
    public void Packs_rules_passed_from_ability_rulesFor()
    {
        Ability ability = new AbilityBuilder().Add(RawRule.Can("read", "Post")).Add(RawRule.Can("delete", "Post")).Build();

        Assert.Equal("""[["read","Post"]]""", PackedRules.Pack(ability.RulesFor("read", "Post")).ToJsonString());
    }

    [Fact]
    public void Converts_array_of_rule_arrays_to_array_of_rule_objects() =>
        Assert.Equal(2, PackedRules.Unpack("""[["read","Post"],["delete","Post"]]""").Count);

    [Fact]
    public void Puts_1st_element_under_action_and_converts_it_to_an_array() =>
        Assert.Equal(["read", "update"], Unpack("""[["read,update","Post"]]""").Action);

    [Fact]
    public void Converts_even_a_single_action_to_an_array() =>
        Assert.Equal(["read"], Unpack("""[["read","Post"]]""").Action);

    [Fact]
    public void Puts_2nd_element_under_subject_and_converts_it_to_an_array() =>
        Assert.Equal(["Post", "Comment"], Unpack("""[["read","Post,Comment"]]""").Subject!);

    [Fact]
    public void Converts_even_a_single_subject_to_an_array() =>
        Assert.Equal(["Post"], Unpack("""[["read","Post"]]""").Subject!);

    [Fact]
    public void Puts_3rd_element_under_conditions() =>
        Assert.Equal("""{"private":true}""", Unpack("""[["read","Post,Comment",{"private":true}]]""").Conditions!.ToJsonString());

    [Fact]
    public void Converts_conditions_to_null_if_its_value_is_0() =>
        Assert.Null(Unpack("""[["read","Post,Comment",0,1]]""").Conditions);

    [Fact]
    public void Puts_4th_element_under_inverted_and_converts_it_to_boolean() =>
        Assert.True(Unpack("""[["read","Post,Comment",0,1]]""").Inverted);

    [Fact]
    public void Converts_inverted_to_boolean_even_if_it_is_not_specified() =>
        Assert.False(Unpack("""[["read","Post,Comment"]]""").Inverted);

    [Fact]
    public void Puts_5th_element_under_fields_and_converts_it_to_an_array() =>
        Assert.Equal(["title", "description"], Unpack("""[["read","Post,Comment",1,0,"title,description"]]""").Fields!);

    [Fact]
    public void Converts_fields_to_null_if_its_value_is_empty() =>
        Assert.Null(Unpack("""[["read","Post,Comment",1,0,""]]""").Fields);

    [Fact]
    public void Puts_6th_element_under_reason() =>
        Assert.Equal("forbidden reason", Unpack("""[["read","Post,Comment",1,0,0,"forbidden reason"]]""").Reason);

    [Fact]
    public void Converts_reason_to_null_if_its_value_is_empty() =>
        Assert.Null(Unpack("""[["read","Post,Comment",1,0,0,""]]""").Reason);

    [Fact]
    public void Round_trips()
    {
        // Not in CASL's spec.
        RawRule[] rules =
        [
            RawRule.Can("read", "Post", new { authorId = 1 }, ["title"]),
            RawRule.Cannot("delete", "Post", reason: "no"),
            new RawRule { Action = ["read", "update"], Subject = ["Post", "Comment"] },
            RawRule.Can("login"),
        ];

        Assert.Equal(rules.Select(r => r.ToJson()), PackedRules.Unpack(PackedRules.Pack(rules)).Select(r => r.ToJson()));
    }
}
