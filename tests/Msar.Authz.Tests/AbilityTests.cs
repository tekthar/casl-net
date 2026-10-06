using System.Text.Json.Nodes;

namespace Msar.Authz.Tests;

public class AbilityTests
{
    private static readonly SubjectRegistry Registry = Subjects.Create();

    private static WorkItem Item(string? assignee = "u1", WorkStatus status = WorkStatus.Open, int priority = 2, Project? project = null, params string[] labels) =>
        new() { Id = 1, AssigneeId = assignee, Status = status, Priority = priority, Project = project, ProjectId = project?.Id, Labels = [.. labels] };

    [Fact]
    public void Allow_rule_with_condition()
    {
        Ability ability = new AbilityBuilder(Registry).Can("update", "WorkItem", new { assigneeId = "u1" }).Build();

        Assert.True(ability.Can("update", Item("u1")));
        Assert.False(ability.Can("update", Item("u2")));
        Assert.False(ability.Can("delete", Item("u1")));
        Assert.True(ability.Can("update", "WorkItem"));
    }

    [Fact]
    public void Deny_overrides_allow_whatever_the_order()
    {
        Ability denyLast = new AbilityBuilder(Registry)
            .Can("read", "WorkItem")
            .Cannot("read", "WorkItem", new { status = "Done" })
            .Build();
        Ability denyFirst = new AbilityBuilder(Registry)
            .Cannot("read", "WorkItem", new { status = "Done" })
            .Can("read", "WorkItem")
            .Build();

        foreach (Ability ability in new[] { denyLast, denyFirst })
        {
            Assert.True(ability.Can("read", Item(status: WorkStatus.Open)));
            Assert.False(ability.Can("read", Item(status: WorkStatus.Done)));
        }
    }

    [Fact]
    public void Manage_and_all_cover_everything()
    {
        Ability ability = new AbilityBuilder(Registry).Can("manage", "all").Build();

        Assert.True(ability.Can("delete", Item()));
        Assert.True(ability.Can("archive", "Project"));
    }

    [Fact]
    public void Unconditional_deny_hides_the_subject_type()
    {
        Ability ability = new AbilityBuilder(Registry).Can("manage", "all").Cannot("delete", "WorkItem").Build();

        Assert.False(ability.Can("delete", "WorkItem"));
        Assert.True(ability.Can("update", "WorkItem"));
    }

    [Fact]
    public void Placeholders_fill_in_values_and_arrays()
    {
        Ability ability = new AbilityBuilder(Registry)
            .WithVariables(new { user = new { id = "u7" } })
            .Add(RawRule.Parse("""{ "action": "read", "subject": "WorkItem", "conditions": { "projectId": { "$in": "${grant.projectIds}" } } }"""),
                new { grant = new { projectIds = new[] { 10, 11 } } })
            .Add(RawRule.Parse("""{ "action": "update", "subject": "WorkItem", "conditions": { "assigneeId": "${user.id}" } }"""))
            .Build();

        Assert.True(ability.Can("read", Item(project: new Project { Id = 11 })));
        Assert.False(ability.Can("read", Item(project: new Project { Id = 12 })));
        Assert.True(ability.Can("update", Item("u7")));
        Assert.Contains("[10,11]", ability.Rules[0].Conditions!.ToJsonString());
    }

    [Fact]
    public void Missing_placeholder_fails_the_build()
    {
        AbilityBuilder builder = new AbilityBuilder(Registry)
            .Add(RawRule.Parse("""{ "action": "read", "subject": "WorkItem", "inverted": true, "conditions": { "assigneeId": "${user.id}" } }"""));

        Assert.Throws<AuthzConfigurationException>(builder.Build);
    }

    [Fact]
    public void Field_rules_follow_casl()
    {
        Ability ability = new AbilityBuilder(Registry)
            .Can("update", "WorkItem", new { assigneeId = "u1" }, "summary", "status")
            .Cannot("update", "WorkItem", new { status = "Done" }, reason: "Done items are frozen", "status")
            .Build();

        WorkItem open = Item("u1");
        WorkItem done = Item("u1", WorkStatus.Done);

        Assert.True(ability.Can("update", open));
        Assert.True(ability.Can("update", open, "status"));
        Assert.False(ability.Can("update", open, "priority"));
        Assert.True(ability.Can("update", done)); // a field-limited deny doesn't block the whole item
        Assert.False(ability.Can("update", done, "status"));
        Assert.Equal(["summary", "status"], ability.PermittedFields("update", open));
        Assert.Equal(["summary"], ability.PermittedFields("update", done));

        ForbiddenException ex = Assert.Throws<ForbiddenException>(() => ability.EnsureCan("update", done, "status"));
        Assert.Equal("Done items are frozen", ex.Message);
    }

    [Fact]
    public void Collections_use_mongodb_array_semantics()
    {
        Ability ability = new AbilityBuilder(Registry)
            .Can("read", "WorkItem", JsonNode.Parse("""{ "labels": "bug" }"""))
            .Can("triage", "WorkItem", JsonNode.Parse("""{ "labels": { "$all": ["bug", "ui"] } }"""))
            .Can("close", "WorkItem", JsonNode.Parse("""{ "labels": { "$nin": ["blocked"] } }"""))
            .Build();

        Assert.True(ability.Can("read", Item(labels: ["ui", "bug"])));
        Assert.False(ability.Can("read", Item(labels: ["ui"])));
        Assert.True(ability.Can("triage", Item(labels: ["bug", "ui", "x"])));
        Assert.False(ability.Can("triage", Item(labels: ["bug"])));
        Assert.False(ability.Can("close", Item(labels: ["blocked"])));
        Assert.True(ability.Can("close", Item(labels: [])));
    }

    [Fact]
    public void ElemMatch_on_related_rows()
    {
        Ability ability = new AbilityBuilder(Registry)
            .Can("read", "WorkItem", JsonNode.Parse("""{ "comments": { "$elemMatch": { "authorId": "u2", "internal": false } } }"""))
            .Build();

        WorkItem item = Item();
        item.Comments = [new Comment { AuthorId = "u2", Internal = true }, new Comment { AuthorId = "u3", Internal = false }];
        Assert.False(ability.Can("read", item));
        item.Comments.Add(new Comment { AuthorId = "u2", Internal = false });
        Assert.True(ability.Can("read", item));
    }

    [Fact]
    public void Missing_navigation_reads_as_null_in_memory()
    {
        Ability ability = new AbilityBuilder(Registry)
            .Can("read", "WorkItem", JsonNode.Parse("""{ "project.archived": false }"""))
            .Can("list", "WorkItem", JsonNode.Parse("""{ "project.orgUnitId": { "$ne": 3 } }"""))
            .Build();

        Assert.False(ability.Can("read", Item(project: null)));
        Assert.True(ability.Can("read", Item(project: new Project { Archived = false })));
        Assert.True(ability.Can("list", Item(project: null)));
    }

    [Theory]
    [InlineData("""{ "passwordHash": "x" }""", "not allowed")]
    [InlineData("""{ "priority": { "$regex": "x" } }""", "not supported")]
    [InlineData("""{ "status": { "$gt": "Open" } }""", "isn't supported")]
    [InlineData("""{ "priority": "high" }""", "can't be used as Int32")]
    [InlineData("""{ "project": { "id": 1 } }""", "dotted paths")]
    public void Unsafe_or_invalid_conditions_are_rejected(string conditions, string message)
    {
        RawRule rule = RawRule.Can("read", "WorkItem", conditions);
        AuthzConfigurationException ex = Assert.Throws<AuthzConfigurationException>(() => new AbilityBuilder(Registry).Add(rule).Build());
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void Validate_accepts_placeholders_without_values()
    {
        RawRule good = RawRule.Parse("""{ "action": "read", "subject": "WorkItem", "conditions": { "projectId": { "$in": "${grant.projectIds}" } } }""");
        RawRule bad = RawRule.Parse("""{ "action": "read", "subject": "WorkItem", "fields": ["secret"] }""");

        Assert.Empty(AbilityBuilder.Validate(good, Registry));
        Assert.Contains("no field 'secret'", AbilityBuilder.Validate(bad, Registry).Single());
    }

    [Fact]
    public void Raw_rules_round_trip_in_casl_format()
    {
        const string json = """{"action":["read","update"],"subject":"WorkItem","conditions":{"status":"Open"},"fields":["summary"],"inverted":true,"reason":"no"}""";
        RawRule rule = RawRule.Parse(json);

        Assert.Equal(["read", "update"], rule.Action);
        Assert.Equal(["WorkItem"], rule.Subject);
        Assert.True(rule.Inverted);
        Assert.Equal(json, rule.ToJson());
    }

    [Fact]
    public void Casl_export_puts_deny_rules_last()
    {
        Ability ability = new AbilityBuilder(Registry)
            .Cannot("read", "WorkItem", new { status = "Done" })
            .Can("read", "WorkItem")
            .Build();

        IReadOnlyList<RawRule> rules = ability.ToCaslRules();
        Assert.False(rules[0].Inverted);
        Assert.True(rules[1].Inverted);
    }
}
