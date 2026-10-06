// Ported from CASL's spec/permitted_fields.spec.ts (https://github.com/stalniy/casl, packages/casl-ability, commit e2b885a),
// MIT License, Copyright (c) 2017-present Sergii Stotskyi.
// CASL's permittedFieldsOf(ability, action, subject, { fieldsFrom }) is ability.PermittedFields(action, subject, fieldsFrom).

using static Casl.Tests.CaslSpec.Spec;

namespace Casl.Tests.CaslSpec;

public class PermittedFieldsSpec
{
    private static IEnumerable<string> FieldsFrom(Rule rule) => rule.Fields ?? ["title", "description"];

    [Fact]
    public void Returns_an_empty_array_for_ability_with_empty_rules() =>
        Assert.Empty(new AbilityBuilder().Build().PermittedFields("read", "Post", FieldsFrom));

    [Fact]
    public void Returns_all_fields_if_none_of_rules_specify_fields() =>
        Assert.Equal(["title", "description"], Define(b => b.Can("read", "Post")).PermittedFields("read", "Post", FieldsFrom));

    [Fact]
    public void Returns_a_unique_array_of_fields_if_there_are_duplicated_fields()
    {
        Ability ability = Define(b =>
        {
            b.Can("read", "Post", null, "title");
            b.Can("read", "Post", new { id = 1 }, "title", "description");
        });

        IReadOnlyList<string> fields = ability.PermittedFields("read", "Post", FieldsFrom);
        Assert.Equal(2, fields.Count);
        Assert.Contains("title", fields);
        Assert.Contains("description", fields);
    }

    [Fact]
    public void Returns_unique_fields_for_direct_and_inverted_rules()
    {
        Ability ability = Define(b =>
        {
            b.Can("read", "Post", null, "title", "description");
            b.Cannot("read", "Post").OnFields("description");
        });

        Assert.Equal(["title"], ability.PermittedFields("read", "Post", FieldsFrom));
    }

    [Fact]
    public void Allows_to_provide_fieldsFrom()
    {
        Ability ability = Define(b => b.Can("read", "Post"));

        Assert.Equal(["title"], ability.PermittedFields("read", "Post", rule => rule.Fields ?? ["title"]));
    }

    private static Ability InstanceSetup(AbilityOptions? options = null) => Define(b =>
    {
        b.Can("read", "Post", null, "title");
        b.Can("read", "Post", new { id = 1 }, "title", "description");
        b.Cannot("read", "Post", new { @private = true }).OnFields("description");
    }, options);

    [Fact]
    public void Returns_fields_for_specific_instance() =>
        Assert.Equal(["title"], InstanceSetup().PermittedFields("read", new Post { Title = "does not match conditions" }, FieldsFrom));

    [Fact]
    public void Returns_fields_for_instance_which_matches_rule_conditions() =>
        Assert.Equal(["title", "description"], InstanceSetup().PermittedFields("read", new Post { Id = 1, Title = "matches conditions" }, FieldsFrom));

    // Not in CASL's spec: the setup's cannot rule, on an instance it matches.
    [Fact]
    public void Inverted_field_rule_removes_fields_from_matching_instance() =>
        Assert.Equal(["title"], InstanceSetup().PermittedFields("read", new Post { Id = 1, Private = true }, FieldsFrom));

    [Fact]
    public void Deny_overrides_removes_denied_fields_whatever_the_order()
    {
        // Not in CASL's spec: with deny-overrides, an earlier field-level cannot still wins.
        Ability ability = Define(b =>
        {
            b.Cannot("read", "Post").OnFields("description");
            b.Can("read", "Post", null, "title", "description");
        }, new AbilityOptions { Semantics = RuleSemantics.DenyOverrides });

        Assert.Equal(["title"], ability.PermittedFields("read", "Post", FieldsFrom));
        Assert.False(ability.Can("read", "Post", "description"));
    }

    [Fact]
    public void Default_fieldsFrom_uses_the_registered_fields()
    {
        // Not in CASL's spec: CASL has no subject registry; a strict registry knows every field.
        SubjectRegistry registry = new SubjectRegistry().Add<Post>("Post", s => s.ConditionFields("private").Fields("title", "description", "author"));
        Ability ability = new AbilityBuilder(registry)
            .Can("read", "Post")
            .Cannot("read", "Post", new { @private = true }, null, "author")
            .Build();

        Assert.Equal(["title", "description", "author"], ability.PermittedFields("read", new Post()));
        Assert.Equal(["title", "description"], ability.PermittedFields("read", new Post { Private = true }));
    }
}
