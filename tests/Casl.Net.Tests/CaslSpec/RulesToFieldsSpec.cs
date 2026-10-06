// Ported from CASL's spec/rulesToFields.spec.ts (https://github.com/stalniy/casl, packages/casl-ability, commit e2b885a),
// MIT License, Copyright (c) 2017-present Sergii Stotskyi.
// CASL's rulesToFields(ability, action, subjectType) is ability.DefaultValues(action, subjectType).

using static Casl.Tests.CaslSpec.Spec;

namespace Casl.Tests.CaslSpec;

public class RulesToFieldsSpec
{
    [Fact]
    public void Returns_an_empty_object_for_an_empty_ability() =>
        Assert.Empty(new AbilityBuilder().Build().DefaultValues("read", "Post"));

    [Fact]
    public void Returns_an_empty_object_if_ability_contains_only_inverted_rules()
    {
        Ability ability = Define(b =>
        {
            b.Cannot("read", "Post", new { id = 5 });
            b.Cannot("read", "Post", new { @private = true });
        });

        Assert.Empty(ability.DefaultValues("read", "Post"));
    }

    [Fact]
    public void Returns_an_empty_object_for_rules_without_conditions() =>
        Assert.Empty(Define(b => b.Can("read", "Post")).DefaultValues("read", "Post"));

    [Fact]
    public void Extracts_field_values_from_direct_rule_conditions()
    {
        Ability ability = Define(b =>
        {
            b.Can("read", "Post", new { id = 5 });
            b.Can("read", "Post", new { @private = true });
        });

        Assert.Equal("""{"private":true,"id":5}""", ability.DefaultValues("read", "Post").ToJsonString());
    }

    [Fact]
    public void Correctly_sets_values_for_fields_declared_with_dot_notation()
    {
        Ability ability = Define(b =>
        {
            b.Can("read", "Post", new { id = 5 });
            b.Can("read", "Post", """{ "state.private": true }""");
        });

        Assert.Equal("""{"state":{"private":true},"id":5}""", ability.DefaultValues("read", "Post").ToJsonString());
    }

    [Fact]
    public void Skips_plain_object_values_ie_mongo_query_expressions()
    {
        Ability ability = Define(b =>
        {
            b.Can("read", "Post", """{ "state": { "$in": ["draft", "review"] } }""");
            b.Can("read", "Post", new { @private = true });
        });

        Assert.Equal("""{"private":true}""", ability.DefaultValues("read", "Post").ToJsonString());
    }

    [Fact]
    public void Skips_forbidden_properties()
    {
        Ability ability = Define(b =>
        {
            b.Can("read", "Post", """{ "__proto__.__pollutedValue__": 1 }""");
            b.Can("read", "Post", """{ "constructor": 1 }""");
            b.Can("read", "Post", """{ "prototype": 2 }""");
        });

        Assert.Equal(["__pollutedValue__"], ability.DefaultValues("read", "Post").Select(p => p.Key));
    }
}
