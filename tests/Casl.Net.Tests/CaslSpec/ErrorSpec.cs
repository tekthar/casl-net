// Ported from CASL's spec/error.spec.ts (https://github.com/stalniy/casl, packages/casl-ability, commit e2b885a),
// MIT License, Copyright (c) 2017-present Sergii Stotskyi.
// CASL's ForbiddenError.from(ability).throwUnlessCan(...) is ability.EnsureCan(...) here, and its static
// setDefaultMessage is the per-ability AbilityOptions.ForbiddenMessage.

namespace Casl.Tests.CaslSpec;

public class ErrorSpec
{
    private static Ability Setup(AbilityOptions? options = null, params RawRule[] more) =>
        (options is null ? new AbilityBuilder() : new AbilityBuilder(options))
            .Add(RawRule.Can("read", "Post"))
            .AddRange(more)
            .Build();

    [Fact]
    public void Raises_forbidden_exception_on_disallowed_action() =>
        Assert.Throws<ForbiddenException>(() => Setup().EnsureCan("archive", "Post"));

    [Fact]
    public void Does_not_raise_forbidden_exception_on_allowed_action() =>
        Setup().EnsureCan("read", "Post");

    [Fact]
    public void Raises_error_with_context_information()
    {
        ForbiddenException error = Assert.Throws<ForbiddenException>(() => Setup().EnsureCan("archive", "Post"));

        Assert.Equal("archive", error.Action);
        Assert.Equal("Post", error.Subject);
        Assert.Equal("Post", error.SubjectType);
    }

    [Fact]
    public void Raises_error_with_message_provided_in_reason_field_of_forbidden_rule()
    {
        const string noCardMessage = "No credit card provided";
        Ability ability = new AbilityBuilder()
            .Add(new RawRule { Action = ["update"], Subject = ["Post"], Inverted = true, Reason = noCardMessage })
            .Build();

        ForbiddenException error = Assert.Throws<ForbiddenException>(() => ability.EnsureCan("update", "Post"));
        Assert.Equal(noCardMessage, error.Message);
        Assert.Equal(noCardMessage, error.Reason);
    }

    [Fact]
    public void Can_raise_error_with_custom_message()
    {
        const string message = "My custom error message";
        ForbiddenException error = Assert.Throws<ForbiddenException>(() => Setup().EnsureCan("update", "Post", message: message));

        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void Correctly_extracts_subject_type_name_from_class_subject_types()
    {
        Ability ability = new AbilityBuilder().Can<Post>("read").Build();

        ForbiddenException error = Assert.Throws<ForbiddenException>(() => ability.EnsureCan("update", typeof(Post)));
        Assert.Equal("Post", error.SubjectType);
        Assert.Equal("Cannot execute \"update\" on \"Post\"", error.Message);
    }

    [Fact]
    public void Sets_default_message_from_function()
    {
        Ability ability = Setup(new AbilityOptions { ForbiddenMessage = e => $"errror -> {e.Action}-{e.SubjectType}" });

        ForbiddenException error = Assert.Throws<ForbiddenException>(() => ability.EnsureCan("update", "Post"));
        Assert.Equal("errror -> update-Post", error.Message);
    }

    [Fact]
    public void UnlessCan_returns_the_error_instead_of_throwing()
    {
        // Not in CASL's spec: CASL's ForbiddenError#unlessCan.
        Ability ability = Setup();

        Assert.Null(ability.UnlessCan("read", "Post"));
        Assert.Equal("archive", ability.UnlessCan("archive", "Post")?.Action);
    }
}
