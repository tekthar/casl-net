// Ported from CASL's spec/builder.spec.ts (https://github.com/stalniy/casl, packages/casl-ability, commit e2b885a),
// MIT License, Copyright (c) 2017-present Sergii Stotskyi.
// Mapping to the original tests, and what was left out and why: docs/casl-spec-port.md.

using static Casl.Tests.CaslSpec.Spec;

namespace Casl.Tests.CaslSpec;

public class BuilderSpec
{
    [Fact]
    public void Allows_to_construct_rules_using_can_and_cannot()
    {
        AbilityBuilder b = new();
        b.Can("read", "Post");
        b.Cannot("read", "User");

        Assert.Equal(["""{"action":"read","subject":"Post"}""", """{"action":"read","subject":"User","inverted":true}"""], Json(b.Rules));
    }

    [Fact]
    public void Allows_to_specify_multiple_actions()
    {
        AbilityBuilder b = new();
        b.Can(["read", "update"], ["Post"]);

        Assert.Equal(["""{"action":["read","update"],"subject":"Post"}"""], Json(b.Rules));
    }

    [Fact]
    public void Allows_to_specify_multiple_subjects()
    {
        AbilityBuilder b = new();
        b.Can(["read"], ["Post", "User"]);

        Assert.Equal(["""{"action":"read","subject":["Post","User"]}"""], Json(b.Rules));
    }

    [Fact]
    public void Allows_to_pass_a_class_as_subject()
    {
        AbilityBuilder b = new();
        b.Can<Post>("read");
        b.Cannot<Post>("read", new { published = false });

        Assert.Equal(
            ["""{"action":"read","subject":"Post"}""", """{"action":"read","subject":"Post","conditions":{"published":false},"inverted":true}"""],
            Json(b.Rules));
    }

    [Fact]
    public void Allows_to_build_claim_based_rules_without_subjects()
    {
        AbilityBuilder b = new();
        b.Can("read");
        b.Can("write");
        b.Cannot("delete");

        Assert.Equal(["""{"action":"read"}""", """{"action":"write"}""", """{"action":"delete","inverted":true}"""], Json(b.Rules));

        Ability ability = b.Build();
        Assert.True(ability.Can("read"));
        Assert.False(ability.Can("delete"));
    }

    [Fact]
    public void Allows_to_define_rules_with_conditions()
    {
        AbilityBuilder b = new();
        b.Can("read", "Post", new { author = "me" });
        b.Cannot("read", "Post", new { @private = true });

        Assert.Equal(
            [
                """{"action":"read","subject":"Post","conditions":{"author":"me"}}""",
                """{"action":"read","subject":"Post","conditions":{"private":true},"inverted":true}""",
            ],
            Json(b.Rules));
    }

    [Fact]
    public void Allows_to_define_rules_with_fields()
    {
        AbilityBuilder b = new();
        b.Can("read", "Post", null, "title", "id");

        Assert.Equal(["""{"action":"read","subject":"Post","fields":["title","id"]}"""], Json(b.Rules));
    }

    [Fact]
    public void Allows_to_define_rules_with_fields_and_conditions()
    {
        AbilityBuilder b = new();
        b.Can("read", "Post", new { @private = true }, "title");

        Assert.Equal(["""{"action":"read","subject":"Post","conditions":{"private":true},"fields":["title"]}"""], Json(b.Rules));
    }

    [Fact]
    public void Allows_to_define_forbidden_rule_with_the_reason()
    {
        const string reason = "is private";
        AbilityBuilder b = new();
        b.Can("read", "Book");
        b.Cannot("read", "Book", new { @private = true }).Because(reason);

        Assert.Equal(
            [
                """{"action":"read","subject":"Book"}""",
                """{"action":"read","subject":"Book","conditions":{"private":true},"inverted":true,"reason":"is private"}""",
            ],
            Json(b.Rules));
    }

    [Fact]
    public void Fluent_where_and_on_fields_refine_the_rule()
    {
        // Not in CASL's spec: the .NET builder's fluent form.
        AbilityBuilder b = new();
        b.Can("read", "Post").Where(new { author = "me" }).OnFields("title");

        Assert.Equal(["""{"action":"read","subject":"Post","conditions":{"author":"me"},"fields":["title"]}"""], Json(b.Rules));
    }

    [Fact]
    public void Defines_ability_using_dsl()
    {
        Ability ability = Define(b =>
        {
            b.Can("read", "Book");
            b.Cannot("read", "Book", new { @private = true });
        });

        Assert.Equal(
            ["""{"action":"read","subject":"Book"}""", """{"action":"read","subject":"Book","conditions":{"private":true},"inverted":true}"""],
            Json(ability.Rules));
    }

    private sealed record Model(string ModelName);

    [Fact]
    public void Accepts_options_for_the_ability()
    {
        AbilityOptions options = new() { DetectSubjectType = subject => subject is Model m ? m.ModelName : null };
        Ability ability = Define(b => b.Can("read", "Book"), options);

        Assert.True(ability.Can("read", new Model("Book")));
    }
}
