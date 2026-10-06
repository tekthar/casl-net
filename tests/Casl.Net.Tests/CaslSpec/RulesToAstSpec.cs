// Ported from CASL's spec/rulesToAST.spec.ts (https://github.com/stalniy/casl, packages/casl-ability, commit e2b885a),
// MIT License, Copyright (c) 2017-present Sergii Stotskyi.
//
// CASL turns rules into a ucast AST and checks that interpreting it agrees with ability.can. Here the same rule
// sets are turned into the LINQ predicate EF Core translates to SQL (Ability.Filter), compiled and run in memory,
// and must agree with ability.Can — under CASL semantics and under deny-overrides. The AST's shape tests become
// ToMongoQuery shape tests.

using static Casl.Tests.CaslSpec.Spec;

namespace Casl.Tests.CaslSpec;

public class RulesToAstSpec
{
    [Fact]
    public void Returns_empty_query_if_there_are_no_conditions() =>
        Assert.Equal("{}", Define(b => b.Can("read", "Post")).ToMongoQuery("read", "Post")?.ToJsonString());

    [Fact]
    public void Returns_null_if_there_is_no_ability_to_do_an_action() =>
        Assert.Null(Define(b => b.Can("read", "Post")).ToMongoQuery("update", "Post"));

    [Fact]
    public void Returns_only_or_if_there_are_no_forbidden_rules()
    {
        Ability ability = Define(b =>
        {
            b.Can("read", "Post", new { author = 1 });
            b.Can("read", "Post", new { @private = false });
        });

        Assert.Equal("""{"$or":[{"private":false},{"author":1}]}""", ability.ToMongoQuery("read", "Post")?.ToJsonString());
    }

    [Fact]
    public void Returns_or_of_ands_if_there_are_forbidden_and_regular_rules()
    {
        Ability ability = Define(b =>
        {
            b.Can("read", "Post", new { author = 1 });
            b.Can("read", "Post", new { sharedWith = 1 });
            b.Cannot("read", "Post", new { @private = true });
        });

        Assert.Equal(
            """{"$or":[{"$and":[{"sharedWith":1},{"$nor":[{"private":true}]}]},{"$and":[{"author":1},{"$nor":[{"private":true}]}]}]}""",
            ability.ToMongoQuery("read", "Post")?.ToJsonString());
    }

    public static TheoryData<string, RuleSemantics> Cases()
    {
        TheoryData<string, RuleSemantics> data = [];
        foreach (string name in Scenarios.Keys)
        {
            data.Add(name, RuleSemantics.Casl);
            data.Add(name, RuleSemantics.DenyOverrides);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Filter_agrees_with_Can(string scenario, RuleSemantics semantics)
    {
        (Action<AbilityBuilder> define, string action, Post[] posts) = Scenarios[scenario];
        Ability ability = Define(define, new AbilityOptions { Semantics = semantics });
        Func<Post, bool> filter = ability.Filter<Post>(action).Compile();

        foreach (Post post in posts)
        {
            Assert.Equal(ability.Can(action, post), filter(post));
        }
    }

    private static readonly Dictionary<string, (Action<AbilityBuilder> Define, string Action, Post[] Posts)> Scenarios = new()
    {
        ["OR-ed regular rules"] = (b =>
        {
            b.Can("read", "Post", new { authorId = 1 });
            b.Can("read", "Post", """{ "status": { "$in": ["review", "published"] } }""");
        }, "read",
        [
            new() { AuthorId = 1, Status = "draft" },
            new() { AuthorId = 2, Status = "published" },
            new() { AuthorId = 2, Status = "archived" },
        ]),

        ["priority ordered rules"] = (b =>
        {
            b.Can("read", "Post", new { ownerId = 1 });
            b.Cannot("read", "Post", new { archived = true });
            b.Can("read", "Post", new { shared = true });
            b.Cannot("read", "Post", new { hidden = true });
            b.Can("read", "Post", new { @public = true });
            b.Cannot("read", "Post", new { flagged = true });
            b.Can("read", "Post", new { status = "pinned" });
        }, "read",
        [
            new() { Status = "pinned", Flagged = true },
            new() { Public = true, Flagged = true },
            new() { Public = true, Hidden = true },
            new() { Shared = true, Hidden = true },
            new() { OwnerId = 1, Archived = true },
            new() { OwnerId = 1 },
            new() { Public = false, Shared = false, OwnerId = 2 },
        ]),

        ["nested priority overrides"] = (b =>
        {
            b.Can("read", "Post");
            b.Cannot("read", "Post", new { author = "me" });
            b.Can("read", "Post", new { author = "me", admin = true });
        }, "read",
        [
            new() { Author = "me", Admin = true },
            new() { Author = "me", Admin = false },
            new() { Author = "someoneelse", Admin = false },
        ]),

        ["general allow followed by specific deny"] = (b =>
        {
            b.Can("read", "Post");
            b.Cannot("read", "Post", new { secret = true });
        }, "read", [new() { Secret = true }, new() { Secret = false }, new() { Title = "Public" }]),

        ["specific deny followed by general allow"] = (b =>
        {
            b.Cannot("read", "Post", new { secret = true });
            b.Can("read", "Post");
        }, "read", [new() { Secret = true }, new() { Secret = false }, new() { Title = "Public" }]),

        ["conditional allow overrides global deny"] = (b =>
        {
            b.Cannot("read", "Post");
            b.Can("read", "Post", new { owner = "me" });
        }, "read", [new() { Owner = "me" }, new() { Owner = "someoneelse" }, new() { Title = "Public" }]),

        ["multiple negative overrides"] = (b =>
        {
            b.Can("read", "Post");
            b.Cannot("read", "Post", new { archived = true });
            b.Cannot("read", "Post", new { locked = true });
        }, "read",
        [
            new() { Archived = true, Locked = true },
            new() { Archived = true, Locked = false },
            new() { Archived = false, Locked = true },
            new() { Archived = false, Locked = false },
        ]),

        ["unconditional deny shadows lower priority allow"] = (b =>
        {
            b.Can("read", "Post");
            b.Cannot("read", "Post");
        }, "read", [new() { Author = "me" }, new() { Secret = false }]),

        ["OR-ed simple and composite conditions"] = (b =>
        {
            b.Can("read", "Post", new { status = "published" });
            b.Can("read", "Post", new { status = "draft", owner = "me" });
        }, "read",
        [
            new() { Status = "published", Owner = "someoneelse" },
            new() { Status = "draft", Owner = "me" },
            new() { Status = "draft", Owner = "someoneelse" },
            new() { Status = "archived", Owner = "me" },
        ]),

        ["unconditional can bounded by higher priority cannot rules"] = (b =>
        {
            b.Can("read", "Post");
            b.Cannot("read", "Post", new { @private = true });
            b.Can("read", "Post", new { authorId = 1 });
        }, "read",
        [
            new() { AuthorId = 1, Private = true },
            new() { AuthorId = 2, Private = true },
            new() { AuthorId = 2, Private = false },
            new() { AuthorId = 2 },
        ]),

        ["unconditional cannot stops lower priority rules"] = (b =>
        {
            b.Can("read", "Post", new { @public = true });
            b.Cannot("read", "Post");
            b.Can("read", "Post", new { authorId = 1 });
        }, "read",
        [
            new() { AuthorId = 1, Public = false },
            new() { AuthorId = 2, Public = true },
            new() { AuthorId = 2, Public = false },
        ]),

        ["higher priority can overrides lower priority cannot"] = (b =>
        {
            b.Cannot("manage", "Post", new { id = 1 });
            b.Can("manage", "Post", new { id = 1 });
        }, "manage", [new() { Id = 1 }, new() { Id = 2 }]),

        ["only cannot rules"] = (b =>
        {
            b.Cannot("read", "Post", new { @private = true });
            b.Cannot("read", "Post", new { archived = true });
        }, "read", [new() { Private = true }, new() { Archived = true }, new() { Private = false, Archived = false }]),

        ["unrelated action and subject rules"] = (b =>
        {
            b.Can("read", "Post", new { published = true });
            b.Cannot("read", "Post", new { archived = true });
            b.Can("update", "Post", new { authorId = 1 });
            b.Cannot("update", "Post", new { locked = true });
            b.Can("read", "Comment", new { approved = true });
        }, "read",
        [
            new() { Published = true, Archived = false },
            new() { Published = true, Archived = true },
            new() { AuthorId = 1, Published = false },
            new() { Approved = true, Published = false },
        ]),

        ["field-specific rules mixed with subject-level rules"] = (b =>
        {
            b.Can("read", "Post", new { authorId = 1 });
            b.Cannot("read", "Post", new { @private = true }, null, "description");
            b.Cannot("read", "Post").OnFields("metadata");
        }, "read",
        [
            new() { AuthorId = 1, Private = true },
            new() { AuthorId = 1, Private = false },
            new() { AuthorId = 2, Private = false },
        ]),

        ["alternating can and cannot rules with different operators"] = (b =>
        {
            b.Can("read", "Post", """{ "score": { "$gte": 10 } }""");
            b.Cannot("read", "Post", """{ "tags": { "$in": ["blocked"] } }""");
            b.Can("read", "Post", """{ "reviewerIds": { "$in": [1] } }""");
            b.Cannot("read", "Post", """{ "deletedAt": { "$exists": true } }""");
            b.Can("read", "Post", """{ "status": { "$in": ["featured", "pinned"] } }""");
        }, "read",
        [
            new() { Score = 12, Tags = ["news"] },
            new() { Score = 12, Tags = ["blocked"] },
            new() { ReviewerIds = [1], DeletedAt = DateTime.UtcNow },
            new() { Status = "featured", DeletedAt = DateTime.UtcNow },
            new() { Status = "draft", ReviewerIds = [2], Score = 1 },
        ]),

        ["higher priority cannot shadows an overlapping can branch"] = (b =>
        {
            b.Can("read", "Post", new { @private = true });
            b.Can("read", "Post", new { draft = false });
            b.Cannot("read", "Post", new { @private = true });
        }, "read",
        [
            new() { Private = true, Draft = false },
            new() { Private = true, Draft = true },
            new() { Private = false, Draft = false },
            new() { Private = false, Draft = true },
        ]),
    };
}
