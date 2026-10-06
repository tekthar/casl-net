// Ported from CASL's spec/ability.spec.ts (https://github.com/stalniy/casl, packages/casl-ability, commit e2b885a),
// MIT License, Copyright (c) 2017-present Sergii Stotskyi.
// Mapping to the original tests, and what was left out and why: docs/casl-spec-port.md.

using static Casl.Tests.CaslSpec.Spec;

namespace Casl.Tests.CaslSpec;

public class AbilitySpec
{
    private static AbilityOptions WithAliases(Dictionary<string, string[]> aliases) => new() { Aliases = ActionAliases.Create(aliases) };

    [Fact]
    public void Allows_to_add_alias_for_actions()
    {
        Ability ability = Define(b => b.Can("modify", "Post"), WithAliases(new() { ["modify"] = ["update", "delete"] }));

        Assert.True(ability.Can("modify", "Post"));
        Assert.True(ability.Can("update", "Post"));
        Assert.True(ability.Can("delete", "Post"));
    }

    [Fact]
    public void Allows_deeply_nested_aliased_actions()
    {
        AbilityOptions options = new() { Aliases = ActionAliases.Create(new Dictionary<string, string> { ["sort"] = "increment", ["modify"] = "sort" }) };
        Ability ability = Define(b => b.Can("modify", "all"), options);

        Assert.True(ability.Can("increment", "123"));
    }

    [Fact]
    public void Throws_when_trying_to_define_manage_alias() =>
        Assert.ThrowsAny<Exception>(() => ActionAliases.Create(new Dictionary<string, string> { ["manage"] = "crud" }));

    [Fact]
    public void Throws_when_trying_to_make_manage_a_part_of_aliased_action() =>
        Assert.ThrowsAny<Exception>(() => ActionAliases.Create(new Dictionary<string, string[]> { ["modify"] = ["crud", "manage"] }));

    [Fact]
    public void Throws_when_trying_to_alias_action_to_itself()
    {
        Assert.ThrowsAny<Exception>(() => ActionAliases.Create(new Dictionary<string, string> { ["sort"] = "sort" }));
        Assert.ThrowsAny<Exception>(() => ActionAliases.Create(new Dictionary<string, string[]> { ["sort"] = ["sort", "order"] }));
    }

    [Fact]
    public void Lists_all_rules()
    {
        Ability ability = Define(b =>
        {
            b.Can("crud", "all");
            b.Can("learn", "Range");
            b.Cannot("read", "String");
            b.Cannot("read", "Hash");
            b.Cannot("preview", "Array");
        });

        Assert.Equal(
            [
                """{"action":"crud","subject":"all"}""",
                """{"action":"learn","subject":"Range"}""",
                """{"action":"read","subject":"String","inverted":true}""",
                """{"action":"read","subject":"Hash","inverted":true}""",
                """{"action":"preview","subject":"Array","inverted":true}""",
            ],
            Json(ability.Rules));
    }

    [Fact]
    public void Allows_to_check_abilities_only_by_action()
    {
        Ability ability = new AbilityBuilder().Add(new RawRule { Action = ["read"] }).Build();

        Assert.True(ability.Can("read"));
    }

    [Fact]
    public void Allows_to_define_custom_anyAction_and_anySubjectType_options()
    {
        AbilityBuilder builder = new(new AbilityOptions { AnyAction = "*", AnySubjectType = "*" });
        Ability ability = builder.Add(new RawRule { Action = ["*"], Subject = ["*"] }).Build();

        Assert.True(ability.Can("read", "Post"));
        Assert.True(ability.Can("update", "Post"));
        Assert.True(ability.Can("doAnythingWith", "Post"));
        Assert.True(ability.Can("*", "*"));
        Assert.True(ability.Can("*", "Post"));
        Assert.True(ability.Can("read", "*"));
    }

    // ---- by default ----

    private static Ability Setup() => Define(b =>
    {
        b.Can("test", "all");
        b.Can(["read", "update"], ["Post"]);
        b.Can("delete", "Post", new { creator = "admin" });
        b.Cannot("publish", "Post");
    });

    [Fact]
    public void Allows_to_perform_specified_actions_on_target_instance()
    {
        Ability ability = Setup();
        Assert.True(ability.Can("read", new Post()));
        Assert.True(ability.Can("update", new Post()));
    }

    [Fact]
    public void Allows_to_perform_specified_actions_on_target_type_string()
    {
        Ability ability = Setup();
        Assert.True(ability.Can("read", "Post"));
        Assert.True(ability.Can("update", "Post"));
    }

    [Fact]
    public void Allows_to_perform_specified_actions_on_target_type_class()
    {
        Ability ability = Define(b =>
        {
            b.Can<Post>("read");
            b.Can<Post>("update", new { authorId = 1 });
        });

        Assert.True(ability.Can("read", typeof(Post)));
        Assert.True(ability.Can("update", typeof(Post)));
        Assert.True(ability.Can("read", new Post()));
        Assert.True(ability.Can("update", new Post { AuthorId = 1 }));
    }

    [Fact]
    public void Disallows_to_perform_unspecified_action_on_target()
    {
        Ability ability = Setup();
        Assert.False(ability.Can("archive", "Post"));
        Assert.False(ability.Can("archive", new Post()));
    }

    [Fact]
    public void Disallows_to_perform_action_if_action_parameter_is_falsy() =>
        Assert.False(Setup().Can(null!, "Post"));

    [Fact]
    public void Checks_by_all_subject_if_subject_parameter_is_falsy() =>
        Assert.True(Setup().Can("test", null));

    [Fact]
    public void Disallows_to_perform_action_on_unspecified_target_type() =>
        Assert.False(Setup().Can("read", "User"));

    [Fact]
    public void Allows_action_if_target_type_matches_at_least_1_rule_with_or_without_conditions() =>
        Assert.True(Setup().Can("delete", "Post"));

    [Fact]
    public void Allows_to_perform_action_if_target_instance_matches_conditions() =>
        Assert.True(Setup().Can("delete", new Post { Creator = "admin" }));

    [Fact]
    public void Disallows_to_perform_action_if_target_instance_does_not_match_conditions() =>
        Assert.False(Setup().Can("delete", new Post { Creator = "user" }));

    [Fact]
    public void Disallows_to_perform_action_for_inverted_rule_when_checks_by_subject_type() =>
        Assert.False(Setup().Can("publish", "Post"));

    // ---- rule precedence ----

    [Fact]
    public void Checks_every_rule_using_logical_or()
    {
        Ability ability = Define(b =>
        {
            b.Can("delete", "Post", new { creator = "me" });
            b.Can("delete", "Post", """{ "sharedWith": { "$in": ["me"] } }""");
        });

        Assert.True(ability.Can("delete", new Post { Creator = "me" }));
        Assert.True(ability.Can("delete", new Post { SharedWith = "me" }));
    }

    [Fact]
    public void Checks_rules_in_inverse_order()
    {
        Ability ability = Define(b =>
        {
            b.Can("delete", "Post", new { creator = "me" });
            b.Cannot("delete", "Post", new { archived = true });
        });

        Assert.False(ability.Can("delete", new Post { Creator = "me", Archived = true }));
        Assert.True(ability.Can("delete", new Post { Creator = "me" }));
    }

    [Fact]
    public void Shadows_rule_with_conditions_by_the_same_rule_without_conditions()
    {
        Ability ability = Define(b =>
        {
            b.Can("delete", "Post");
            b.Can("delete", "Post", new { creator = "me" });
        });

        Assert.True(ability.Can("delete", new Post { Creator = "someoneelse" }));
        Assert.True(ability.Can("delete", new Post { Creator = "me" }));
    }

    [Fact]
    public void Does_not_shadow_rule_with_conditions_if_the_last_one_is_disallowed_by_cannot()
    {
        Ability ability = Define(b =>
        {
            b.Can("manage", "Post");
            b.Cannot("delete", "Post");
            b.Can("delete", "Post", new { creator = "me" });
        });

        Assert.False(ability.Can("delete", new Post { Creator = "someoneelse" }));
        Assert.True(ability.Can("delete", new Post { Creator = "me" }));
    }

    [Fact]
    public void Shadows_inverted_rule_by_regular_one()
    {
        Ability ability = Define(b =>
        {
            b.Cannot("delete", "Post", new { creator = "me" });
            b.Can("delete", "Post", new { creator = "me" });
        });

        Assert.True(ability.Can("delete", new Post { Creator = "me" }));
    }

    [Fact]
    public void Shadows_all_subject_rule_by_specific_one()
    {
        Ability ability = Define(b =>
        {
            b.Can("delete", "all");
            b.Cannot("delete", "Post");
        });

        Assert.False(ability.Can("delete", "Post"));
        Assert.True(ability.Can("delete", "User"));
    }

    [Fact]
    public void Treats_empty_conditions_as_always_matching()
    {
        Ability ability = Define(b =>
        {
            b.Can("manage", "User", new { });
            b.Cannot("update", "User", new { }).OnFields("admin");
        });

        Assert.False(ability.Can("update", "User", "admin"));
        Assert.True(ability.Can("update", "User", "name"));
    }

    // ---- rule conditions ----

    [Fact]
    public void Allows_to_use_equality_conditions()
    {
        Ability ability = Define(b => b.Can("read", "Post", new { creator = "me" }));

        Assert.True(ability.Can("read", new Post { Creator = "me" }));
        Assert.False(ability.Can("read", new Post { Creator = "someoneelse" }));
    }

    [Fact]
    public void Allows_to_use_mongo_like_ne_condition()
    {
        Ability ability = Define(b => b.Can("read", "Post", """{ "creator": { "$ne": "me" } }"""));

        Assert.False(ability.Can("read", new Post { Creator = "me" }));
        Assert.True(ability.Can("read", new Post { Creator = "someoneelse" }));
    }

    [Fact]
    public void Allows_to_use_mongo_like_in_condition()
    {
        // CASL's test uses one `state` property holding a string or an array; .NET needs two properties.
        Ability ability = Define(b =>
        {
            b.Can("read", "Post", """{ "state": { "$in": ["shared", "draft"] } }""");
            b.Can("read", "Post", """{ "states": { "$in": ["shared", "draft"] } }""");
        });

        Assert.True(ability.Can("read", new Post { State = "draft" }));
        Assert.True(ability.Can("read", new Post { State = "shared" }));
        Assert.True(ability.Can("read", new Post { States = ["shared", "public"] }));
        Assert.False(ability.Can("read", new Post { State = "public", States = ["public"] }));
    }

    [Fact]
    public void Allows_to_use_mongo_like_all_condition()
    {
        Ability ability = Define(b => b.Can("read", "Post", """{ "states": { "$all": ["shared", "draft"] } }"""));

        Assert.False(ability.Can("read", new Post { States = ["draft"] }));
        Assert.False(ability.Can("read", new Post { States = ["shared"] }));
        Assert.True(ability.Can("read", new Post { States = ["shared", "draft"] }));
    }

    [Fact]
    public void Allows_to_use_mongo_like_gt_and_gte_condition()
    {
        Ability ability = Define(b =>
        {
            b.Can("update", "Post", """{ "views": { "$gt": 10 } }""");
            b.Cannot("update", "Post", """{ "views": { "$gte": 1000 } }""");
        });

        Assert.False(ability.Can("update", new Post { Views = 9 }));
        Assert.True(ability.Can("update", new Post { Views = 100 }));
        Assert.False(ability.Can("update", new Post { Views = 1001 }));
    }

    [Fact]
    public void Allows_to_use_mongo_like_lt_and_lte_condition()
    {
        Ability ability = Define(b =>
        {
            b.Can("update", "Post", """{ "views": { "$lt": 5 } }""");
            b.Cannot("update", "Post", """{ "views": { "$lte": 2 } }""");
        });

        Assert.False(ability.Can("update", new Post { Views = 2 }));
        Assert.True(ability.Can("update", new Post { Views = 3 }));
    }

    [Fact]
    public void Allows_to_use_mongo_like_exists_condition()
    {
        Ability ability = Define(b => b.Can("read", "Post", """{ "views": { "$exists": true } }"""));

        Assert.False(ability.Can("read", new Post()));
        Assert.True(ability.Can("read", new Post { Views = 3 }));
    }

    [Fact]
    public void Allows_to_use_mongo_like_dot_notation_conditions()
    {
        Ability ability = Define(b =>
        {
            b.Can("delete", "Post", """{ "authors.0": { "$exists": false } }""");
            b.Can("update", "Post", """{ "comments.author": "Ted" }""");
        });

        Assert.False(ability.Can("delete", new Post { Authors = [new() { Id = "me" }, new() { Id = "someoneelse" }] }));
        Assert.True(ability.Can("delete", new Post { Authors = [] }));
        Assert.True(ability.Can("update", new Post { Comments = [new() { Author = "Ted" }, new() { Author = "John" }] }));
        Assert.False(ability.Can("update", new Post { Comments = [new() { Author = "John" }] }));
    }

    [Fact]
    public void Allows_to_use_mongo_like_regex_condition()
    {
        Ability ability = Define(b => b.Can("delete", "Post", """{ "title": { "$regex": "\\[DELETED\\]" } }"""));

        Assert.False(ability.Can("delete", new Post { Title = "public" }));
        Assert.False(ability.Can("delete", new Post { Title = "[deleted] title" }));
        Assert.True(ability.Can("delete", new Post { Title = "[DELETED] title" }));
    }

    [Fact]
    public void Allows_to_use_mongo_like_regex_condition_with_options()
    {
        // Not in CASL's spec: MongoDB's $options, which CASL supports through ucast.
        Ability ability = Define(b => b.Can("delete", "Post", """{ "title": { "$regex": "^\\[deleted\\]", "$options": "i" } }"""));

        Assert.True(ability.Can("delete", new Post { Title = "[DELETED] title" }));
        Assert.False(ability.Can("delete", new Post { Title = "title [deleted]" }));
        Assert.False(ability.Can("delete", new Post()));
    }

    [Fact]
    public void Allows_to_use_mongo_like_elemMatch_condition()
    {
        Ability ability = Define(b => b.Can("delete", "Post", """{ "authors": { "$elemMatch": { "id": "me-id" } } }"""));

        Assert.False(ability.Can("delete", new Post { Authors = [new() { Id = "someone-else-id" }] }));
        Assert.True(ability.Can("delete", new Post { Authors = [new() { Id = "me-id" }] }));
        Assert.True(ability.Can("delete", new Post { Authors = [new() { Id = "someone-else-id" }, new() { Id = "me-id" }] }));
    }

    [Fact]
    public void Allows_to_use_mongo_like_size_condition()
    {
        // Not in CASL's spec: MongoDB's $size, which CASL supports through ucast.
        Ability ability = Define(b => b.Can("delete", "Post", """{ "authors": { "$size": 0 } }"""));

        Assert.True(ability.Can("delete", new Post { Authors = [] }));
        Assert.False(ability.Can("delete", new Post { Authors = [new() { Id = "me" }] }));
        Assert.False(ability.Can("delete", new Post()));
    }

    [Fact]
    public void Returns_true_for_ability_with_inverted_rule_and_subject_specified_as_string()
    {
        Ability ability = Define(b =>
        {
            b.Can("read", "Post");
            b.Cannot("read", "Post", new { @private = true });
        });

        Assert.True(ability.Can("read", "Post"));
    }

    // ---- per field abilities ----

    [Fact]
    public void Allows_to_define_per_field_rules()
    {
        Ability ability = Define(b => b.Can("read", "Post", null, "title"));

        Assert.True(ability.Can("read", "Post"));
        Assert.True(ability.Can("read", "Post", "title"));
        Assert.False(ability.Can("read", "Post", "description"));
    }

    [Fact]
    public void Allows_to_define_rules_for_several_fields()
    {
        Ability ability = Define(b => b.Can("read", "Post").OnFields("title", "id"));

        Assert.True(ability.Can("read", "Post"));
        Assert.True(ability.Can("read", "Post", "title"));
        Assert.True(ability.Can("read", "Post", "id"));
        Assert.False(ability.Can("read", "Post", "description"));
    }

    [Fact]
    public void Allows_to_define_inverted_rules_for_a_field()
    {
        Ability ability = Define(b =>
        {
            b.Can("read", "Post");
            b.Cannot("read", "Post").OnFields("description");
        });

        Assert.True(ability.Can("read", "Post"));
        Assert.True(ability.Can("read", "Post", "title"));
        Assert.False(ability.Can("read", "Post", "description"));
    }

    [Fact]
    public void Allows_to_perform_actions_on_all_attributes_if_none_is_specified()
    {
        Ability ability = Define(b => b.Can("read", "Post"));

        Assert.True(ability.Can("read", "Post", "title"));
        Assert.True(ability.Can("read", "Post", "description"));
    }

    [Fact]
    public void Throws_if_there_is_a_rule_with_fields_property_to_be_an_empty_array()
    {
        CaslConfigurationException ex = Assert.Throws<CaslConfigurationException>(() => Define(b => b.Can("read", "Post").OnFields()));
        Assert.Contains("`rawRule.fields` array cannot be empty", ex.Message);
    }

    [Fact]
    public void Field_pattern_author_star_matches_any_1st_level_field()
    {
        Ability ability = Define(b => b.Can("read", "Post").OnFields("author.*"));

        Assert.True(ability.Can("read", "Post", "author"));
        Assert.True(ability.Can("read", "Post", "author.*"));
        Assert.True(ability.Can("read", "Post", "author.name"));
        Assert.True(ability.Can("read", "Post", "author.age"));
        Assert.False(ability.Can("read", "Post", "author.publication.name"));
    }

    [Fact]
    public void Field_pattern_author_double_star_matches_any_depth()
    {
        Ability ability = Define(b => b.Can("read", "Post").OnFields("author.**"));

        Assert.True(ability.Can("read", "Post", "author"));
        Assert.True(ability.Can("read", "Post", "author.**"));
        Assert.True(ability.Can("read", "Post", "author.name"));
        Assert.True(ability.Can("read", "Post", "author.age"));
        Assert.True(ability.Can("read", "Post", "author.publication.name"));
    }

    [Fact]
    public void Field_pattern_star_in_the_middle()
    {
        Ability ability = Define(b => b.Can("read", "Post").OnFields("author.*.name"));

        Assert.False(ability.Can("read", "Post", "author"));
        Assert.True(ability.Can("read", "Post", "author.*.name"));
        Assert.True(ability.Can("read", "Post", "author.publication.name"));
        Assert.False(ability.Can("read", "Post", "author.publication.startDate"));
        Assert.False(ability.Can("read", "Post", "author.publication.country.name"));
    }

    [Fact]
    public void Field_pattern_double_star_in_the_middle()
    {
        Ability ability = Define(b => b.Can("read", "Post").OnFields("author.**.name"));

        Assert.False(ability.Can("read", "Post", "author"));
        Assert.True(ability.Can("read", "Post", "author.**.name"));
        Assert.True(ability.Can("read", "Post", "author.publication.name"));
        Assert.False(ability.Can("read", "Post", "author.publication.startDate"));
        Assert.True(ability.Can("read", "Post", "author.publication.country.name"));
    }

    [Fact]
    public void Field_pattern_star_at_the_beginning()
    {
        Ability ability = Define(b => b.Can("read", "Post").OnFields("*.name"));

        Assert.True(ability.Can("read", "Post", "author.name"));
        Assert.True(ability.Can("read", "Post", "*.name"));
        Assert.False(ability.Can("read", "Post", "author.publication.name"));
    }

    [Fact]
    public void Field_pattern_double_star_at_the_beginning()
    {
        Ability ability = Define(b => b.Can("read", "Post").OnFields("**.name"));

        Assert.True(ability.Can("read", "Post", "author.name"));
        Assert.True(ability.Can("read", "Post", "**.name"));
        Assert.True(ability.Can("read", "Post", "author.publication.name"));
    }

    [Fact]
    public void Field_pattern_trailing_stars()
    {
        Ability ability = Define(b => b.Can("read", "Post").OnFields("author.address.street*"));

        Assert.True(ability.Can("read", "Post", "author.address.street"));
        Assert.True(ability.Can("read", "Post", "author.address.street1"));
        Assert.True(ability.Can("read", "Post", "author.address.street2"));
        Assert.False(ability.Can("read", "Post", "author.address"));
    }

    [Fact]
    public void Field_pattern_correctly_works_with_special_regexp_symbols()
    {
        Ability ability = Define(b => b.Can("read", "Post").OnFields("author?.address+.street*"));

        Assert.True(ability.Can("read", "Post", "author?.address+.street"));
        Assert.True(ability.Can("read", "Post", "author?.address+.street1"));
        Assert.True(ability.Can("read", "Post", "author?.address+.street2"));
        Assert.False(ability.Can("read", "Post", "author?.address+"));
    }

    [Fact]
    public void Field_pattern_can_match_field_patterns()
    {
        Ability ability = Define(b => b.Can("read", "Post").OnFields("vehicle.*.generic.*"));

        Assert.True(ability.Can("read", "Post", "vehicle.profile.generic.item"));
        Assert.True(ability.Can("read", "Post", "vehicle.*.generic.signal"));
        Assert.True(ability.Can("read", "Post", "vehicle.profile.generic.*"));
        Assert.False(ability.Can("read", "Post", "vehicle.*.user.*"));
    }

    // ---- per field abilities, when conditions defined ----

    private static readonly Post MyPost = new() { Author = "me", Published = true };

    private static Ability FieldsWithConditions() =>
        Define(b => b.Can("read", "Post", new { author = MyPost.Author, published = true }, "title", "description"));

    [Fact]
    public void With_conditions_allows_action_on_subject_specified_as_string() =>
        Assert.True(FieldsWithConditions().Can("read", "Post"));

    [Fact]
    public void With_conditions_allows_action_on_subject_field_both_specified_as_strings()
    {
        Ability ability = FieldsWithConditions();
        Assert.True(ability.Can("read", "Post", "title"));
        Assert.True(ability.Can("read", "Post", "description"));
    }

    [Fact]
    public void With_conditions_disallows_action_on_instance_which_mismatches_conditions() =>
        Assert.False(FieldsWithConditions().Can("read", new Post()));

    [Fact]
    public void With_conditions_allows_action_on_instance_which_matches_conditions() =>
        Assert.True(FieldsWithConditions().Can("read", MyPost));

    [Fact]
    public void With_conditions_allows_action_on_instance_field_if_instance_matches()
    {
        Ability ability = FieldsWithConditions();
        Assert.True(ability.Can("read", MyPost, "title"));
        Assert.True(ability.Can("read", MyPost, "description"));
    }

    [Fact]
    public void With_conditions_disallows_field_not_in_the_list() =>
        Assert.False(FieldsWithConditions().Can("read", MyPost, "id"));

    [Fact]
    public void With_conditions_ensures_that_both_conditions_are_met()
    {
        Ability ability = FieldsWithConditions();
        Assert.True(ability.Can("read", MyPost));
        Assert.False(ability.Can("read", new Post { Author = "me", Active = false }));
    }

    [Fact]
    public void With_conditions_relevant_rule_exposes_its_conditions()
    {
        // Adapted from "has rules with `ast` property": the parsed form is internal; the conditions are public.
        Rule? rule = FieldsWithConditions().RelevantRuleFor("read", "Post");
        Assert.Equal("""{"author":"me","published":true}""", rule?.Conditions?.ToJsonString());
    }

    // ---- manage action ----

    [Fact]
    public void Manage_is_an_alias_for_any_action()
    {
        Ability ability = Define(b => b.Can("manage", "all"));

        Assert.True(ability.Can("read", "post"));
        Assert.True(ability.Can("do_whatever_anywhere", "post"));
    }

    [Fact]
    public void Manage_honours_cannot_rules()
    {
        Ability ability = Define(b =>
        {
            b.Can("manage", "all");
            b.Cannot("read", "post");
        });

        Assert.False(ability.Can("read", "post"));
        Assert.True(ability.Can("update", "post"));
    }

    [Fact]
    public void Manage_can_be_used_with_cannot()
    {
        Ability ability = Define(b =>
        {
            b.Can("read", "post");
            b.Cannot("manage", "all");
        });

        Assert.False(ability.Can("read", "post"));
        Assert.False(ability.Can("delete", "post"));
    }

    [Fact]
    public void Manage_honours_field_specific_rules()
    {
        Ability ability = Define(b => b.Can("manage", "all").OnFields("subject"));

        Assert.True(ability.Can("read", "post", "subject"));
    }

    // ---- rulesFor ----

    [Fact]
    public void RulesFor_returns_rules_for_specific_subject_and_action()
    {
        Ability ability = Define(b =>
        {
            b.Can("read", "Post");
            b.Can("update", "Post");
            b.Cannot("read", "Post", new { @private = true });
        });

        Assert.Equal(
            [
                """{"action":"read","subject":"Post","conditions":{"private":true},"inverted":true}""",
                """{"action":"read","subject":"Post"}""",
            ],
            Json(ability.RulesFor("read", "Post")));
    }

    [Fact]
    public void RulesFor_does_not_return_inverted_rules_with_fields_for_subject_and_action()
    {
        Ability ability = Define(b =>
        {
            b.Can("read", "Post");
            b.Cannot("read", "Post").OnFields("title");
        });

        Assert.Equal(["""{"action":"read","subject":"Post"}"""], Json(ability.RulesFor("read", "Post")));
    }

    [Fact]
    public void RulesFor_returns_rules_for_specific_subject_action_and_field()
    {
        Ability ability = Define(b =>
        {
            b.Can("read", "Post");
            b.Cannot("read", "Post").OnFields("title");
        });

        Assert.Equal(
            [
                """{"action":"read","subject":"Post","fields":["title"],"inverted":true}""",
                """{"action":"read","subject":"Post"}""",
            ],
            Json(ability.RulesFor("read", "Post", "title")));
    }

    // ---- actionsFor ----

    [Fact]
    public void ActionsFor_returns_all_actions_associated_with_subject_type()
    {
        Ability ability = Define(b =>
        {
            b.Can("read", "Post");
            b.Can("update", "Post");
            b.Cannot("read", "Post", new { @private = true });
        });

        Assert.Equal(["read", "update"], ability.ActionsFor("Post"));
    }

    [Fact]
    public void ActionsFor_includes_aliases()
    {
        Ability ability = Define(b => b.Can("modify", "Post"), WithAliases(new() { ["modify"] = ["read", "update"] }));

        Assert.Equal(["modify", "read", "update"], ability.ActionsFor("Post"));
    }

    [Fact]
    public void ActionsFor_includes_actions_of_the_all_subject_type()
    {
        Ability ability = Define(b =>
        {
            b.Can("read", "all");
            b.Can("update", "Post");
        });

        Assert.Equal(["update", "read"], ability.ActionsFor("Post"));
    }

    [Fact]
    public void ActionsFor_returns_all_subject_actions_if_there_are_none_for_the_type() =>
        Assert.Equal(["read"], Define(b => b.Can("read", "all")).ActionsFor("Post"));

    [Fact]
    public void ActionsFor_does_not_include_actions_from_possibleRulesFor_misses()
    {
        Ability ability = Define(b => b.Can("read", "Post"));
        _ = ability.PossibleRulesFor("archive", "Post");

        Assert.Equal(["read"], ability.ActionsFor("Post"));
    }

    [Fact]
    public void ActionsFor_returns_empty_without_rules() =>
        Assert.Empty(new AbilityBuilder().Build().ActionsFor("Post"));
}
