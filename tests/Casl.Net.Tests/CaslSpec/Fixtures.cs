// Ported from CASL's specs (https://github.com/stalniy/casl, packages/casl-ability/spec, commit e2b885a),
// MIT License, Copyright (c) 2017-present Sergii Stotskyi.
//
// CASL's specs use plain JavaScript objects with whatever properties a test needs. .NET needs a class, so
// Post carries every property the ported specs use; unset properties stay null, as missing ones do in JavaScript.

namespace Casl.Tests.CaslSpec;

public sealed class Post
{
    public int? Id { get; set; }
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Author { get; set; }
    public int? AuthorId { get; set; }
    public bool? Published { get; set; }
    public string? Creator { get; set; }
    public string? SharedWith { get; set; }
    public bool? Archived { get; set; }
    public string? State { get; set; }
    public List<string>? States { get; set; }
    public int? Views { get; set; }
    public List<Person>? Authors { get; set; }
    public List<PostComment>? Comments { get; set; }
    public bool? Private { get; set; }
    public bool? Active { get; set; }
    public string? Status { get; set; }
    public string? Owner { get; set; }
    public int? OwnerId { get; set; }
    public bool? Admin { get; set; }
    public bool? Secret { get; set; }
    public bool? Locked { get; set; }
    public bool? Hidden { get; set; }
    public bool? Shared { get; set; }
    public bool? Public { get; set; }
    public bool? Flagged { get; set; }
    public bool? Groupped { get; set; }
    public bool? Draft { get; set; }
    public bool? Approved { get; set; }
    public int? Score { get; set; }
    public List<string> Tags { get; set; } = [];
    public List<int> ReviewerIds { get; set; } = [];
    public DateTime? DeletedAt { get; set; }
}

public sealed class Person
{
    public string? Id { get; set; }
    public string? Name { get; set; }
}

public sealed class PostComment
{
    public string? Author { get; set; }
}

internal static class Spec
{
    /// <summary>CASL's <c>defineAbility((can, cannot) =&gt; ...)</c>.</summary>
    public static Ability Define(Action<AbilityBuilder> define, AbilityOptions? options = null)
    {
        AbilityBuilder builder = options is null ? new AbilityBuilder() : new AbilityBuilder(options);
        define(builder);
        return builder.Build();
    }

    /// <summary>CASL's <c>ruleToObject</c>: the rule as JSON, without empty properties.</summary>
    public static string Json(Rule rule) => rule.Origin.ToJson();

    public static string[] Json(IEnumerable<Rule> rules) => rules.Select(Json).ToArray();

    public static string[] Json(IEnumerable<RawRule> rules) => rules.Select(r => r.ToJson()).ToArray();
}
