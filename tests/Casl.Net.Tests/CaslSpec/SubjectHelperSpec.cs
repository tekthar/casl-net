// Ported from CASL's spec/subject_helper.spec.ts (https://github.com/stalniy/casl, packages/casl-ability, commit e2b885a),
// MIT License, Copyright (c) 2017-present Sergii Stotskyi.

namespace Casl.Tests.CaslSpec;

public class SubjectHelperSpec
{
    [Fact]
    public void Defines_subject_type_for_an_object()
    {
        SubjectInstance? tagged = Subject.Of("Article", new { });

        Assert.Equal("Article", new AbilityBuilder().Build().DetectSubjectType(tagged));
    }

    [Fact]
    public void Throws_when_trying_to_redefine_subject_type()
    {
        SubjectInstance? tagged = Subject.Of("Article", new { });

        Assert.Throws<InvalidOperationException>(() => Subject.Of("User", tagged));
    }

    [Fact]
    public void Does_not_throw_if_subject_type_equals_provided_subject_type()
    {
        SubjectInstance? tagged = Subject.Of("Article", new { });

        Assert.Same(tagged, Subject.Of("Article", tagged));
    }

    [Fact]
    public void Ignores_null_subjects() => Assert.Null(Subject.Of("Test", null));

    [Fact]
    public void Tagged_objects_are_checked_against_their_tag()
    {
        // Not in CASL's helper spec; it is how CASL's rulesToAST spec passes its plain objects.
        Ability ability = new AbilityBuilder().Can("read", "Article", new { published = true }).Build();

        Assert.True(ability.Can("read", Subject.Of("Article", new { published = true })));
        Assert.False(ability.Can("read", Subject.Of("Article", new { published = false })));
        Assert.False(ability.Can("read", Subject.Of("User", new { published = true })));
    }
}
