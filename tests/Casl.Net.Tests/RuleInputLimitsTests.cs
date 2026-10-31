using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Casl.Tests;

/// <summary>Rule input from a rule author must fail closed and must not be able to take the host down.</summary>
public sealed class RuleInputLimitsTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    private async Task<TestDb> SeededDb()
    {
        _connection.Open();
        TestDb db = new(new DbContextOptionsBuilder<TestDb>().UseSqlite(_connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.WorkItems.AddRange(Enumerable.Range(1, 6).Select(i => new WorkItem { Id = i, Summary = $"item {i}", Priority = i }));
        await db.SaveChangesAsync();
        return db;
    }

    private static async Task<(List<int> Sql, List<int> Memory)> Compare(TestDb db, Ability ability)
    {
        List<int> sql = await db.WorkItems.AccessibleBy(ability, "read").Select(w => w.Id).OrderBy(id => id).ToListAsync();
        List<WorkItem> all = await db.WorkItems.AsNoTracking().ToListAsync();
        return (sql, all.Where(w => ability.Can("read", w)).Select(w => w.Id).OrderBy(id => id).ToList());
    }

    private static RawRule WideRule(int width, int match) => new()
    {
        Action = ["read"],
        Subject = ["WorkItem"],
        Conditions = new JsonObject
        {
            ["$or"] = new JsonArray(Enumerable.Range(1000, width - 1).Select(p => (JsonNode?)new JsonObject { ["priority"] = p })
                .Append(new JsonObject { ["priority"] = match }).ToArray()),
        },
    };

    private static Ability ManyRules(int count, RuleSemantics semantics)
    {
        AbilityBuilder builder = new(Subjects.Create(), new AbilityOptions { Semantics = semantics });
        builder.AddRange(Enumerable.Range(1000, count).Select(p => RawRule.Can("read", "WorkItem", new { priority = p })));
        builder.Can("read", "WorkItem", new { priority = 2 });
        builder.Can("read", "WorkItem", new { priority = 5 });
        builder.Cannot("read", "WorkItem", new { priority = 5 });
        return builder.Build();
    }

    // A left-deep chain of 20,000 alternatives overflowed the stack in EF Core's expression visitors while the query
    // was translated, which ends the whole process. Built as a balanced tree it is about 15 levels deep.
    // (SQLite itself refuses statements nested more than 1000 deep, with an exception, so the SQL comparison for
    // these sizes runs on PostgreSQL, in Casl.Net.PostgresTests.)
    [Fact]
    public async Task A_rule_with_twenty_thousand_alternatives_translates_to_sql()
    {
        RawRule rule = WideRule(20_000, match: 3);
        Assert.Empty(AbilityBuilder.Validate(rule, Subjects.Create()));
        Ability ability = new AbilityBuilder(Subjects.Create()).Add(rule).Build();

        await using TestDb db = await SeededDb();
        Assert.Contains("\"Priority\"", db.WorkItems.AccessibleBy(ability, "read").ToQueryString());
        Assert.Equal([3], (await db.WorkItems.AsNoTracking().ToListAsync()).Where(w => ability.Can("read", w)).Select(w => w.Id));
    }

    [Theory]
    [InlineData(RuleSemantics.Casl)]
    [InlineData(RuleSemantics.DenyOverrides)]
    public async Task Ten_thousand_rules_translate_to_sql(RuleSemantics semantics)
    {
        Ability ability = ManyRules(10_000, semantics);

        await using TestDb db = await SeededDb();
        Assert.Contains("\"Priority\"", db.WorkItems.AccessibleBy(ability, "read").ToQueryString());
        Assert.Equal([2], (await db.WorkItems.AsNoTracking().ToListAsync()).Where(w => ability.Can("read", w)).Select(w => w.Id));
    }

    // Balanced trees keep the meaning: the database and the in-memory check still agree.
    [Theory]
    [InlineData(RuleSemantics.Casl)]
    [InlineData(RuleSemantics.DenyOverrides)]
    public async Task Wide_rules_and_many_rules_agree_with_the_in_memory_check(RuleSemantics semantics)
    {
        await using TestDb db = await SeededDb();

        (List<int> sql, List<int> memory) = await Compare(db, new AbilityBuilder(Subjects.Create()).Add(WideRule(300, match: 3)).Build());
        Assert.Equal([3], sql);
        Assert.Equal(sql, memory);

        (sql, memory) = await Compare(db, ManyRules(300, semantics));
        Assert.Equal([2], sql);
        Assert.Equal(sql, memory);
    }

    [Fact]
    public void Wide_and_conditions_keep_their_meaning()
    {
        JsonArray all = new(Enumerable.Range(0, 5_000).Select(_ => (JsonNode?)new JsonObject { ["priority"] = new JsonObject { ["$gte"] = 2 } })
            .Append(new JsonObject { ["priority"] = new JsonObject { ["$lt"] = 4 } }).ToArray());
        Ability ability = new AbilityBuilder(Subjects.Create())
            .Add(new RawRule { Action = ["read"], Subject = ["WorkItem"], Conditions = new JsonObject { ["$and"] = all } })
            .Build();

        Assert.Equal([false, true, true, false], new[] { 1, 2, 3, 4 }.Select(p => ability.Can("read", new WorkItem { Priority = p })));
    }

    // A packed rule whose elements have the wrong type must not be read as a wider rule.
    [Theory]
    [InlineData("""[["read","WorkItem","{\"assigneeId\":\"u1\"}"]]""")] // conditions as text would become "no conditions"
    [InlineData("""[["read","WorkItem",["assigneeId"]]]""")]
    [InlineData("""[["read","WorkItem",true]]""")]
    [InlineData("""[["read","WorkItem",{"status":"Done"},"1"]]""")] // inverted flags that would become "allow"
    [InlineData("""[["read","WorkItem",{"status":"Done"},2]]""")]
    [InlineData("""[["read","WorkItem",{"status":"Done"},1.0]]""")]
    [InlineData("""[["read","WorkItem",0,0,["summary"]]]""")] // fields as an array would become "every field"
    [InlineData("""[["read","WorkItem",0,0,true]]""")]
    [InlineData("""[["delete",["WorkItem"]]]""")] // subjects as an array would become "every subject"
    [InlineData("""[["delete",0]]""")]
    [InlineData("""[[["read"],"WorkItem"]]""")]
    [InlineData("""[["","WorkItem"]]""")]
    [InlineData("""[[]]""")]
    [InlineData("""[["read","WorkItem",0,0,0,5]]""")]
    [InlineData("""[["read","WorkItem",0,0,0,"",7]]""")]
    public void Malformed_packed_rules_fail(string packed) =>
        Assert.Throws<CaslConfigurationException>(() => PackedRules.Unpack(packed));

    [Fact]
    public void Packed_rules_in_every_shape_packRules_writes_are_read()
    {
        IReadOnlyList<RawRule> rules = PackedRules.Unpack("""
            [["read","WorkItem"],
             ["read","WorkItem",{"status":"Done"},1],
             ["read","WorkItem",0,0,"summary,status"],
             ["read","WorkItem",0,1,0,"no"],
             ["login",null,0,1],
             ["read","WorkItem",null,true],
             ["read","WorkItem",0,false,"",""]]
            """);

        Assert.Equal(
            [
                """{"action":"read","subject":"WorkItem"}""",
                """{"action":"read","subject":"WorkItem","conditions":{"status":"Done"},"inverted":true}""",
                """{"action":"read","subject":"WorkItem","fields":["summary","status"]}""",
                """{"action":"read","subject":"WorkItem","inverted":true,"reason":"no"}""",
                """{"action":"login","inverted":true}""",
                """{"action":"read","subject":"WorkItem","inverted":true}""",
                """{"action":"read","subject":"WorkItem"}""",
            ],
            rules.Select(r => r.ToJson()));
    }

    [Fact]
    public void A_packed_cannot_rule_still_denies()
    {
        Ability ability = new AbilityBuilder(Subjects.Create())
            .AddRange(PackedRules.Unpack("""[["read","WorkItem"],["read","WorkItem",{"status":"Done"},1]]"""))
            .Build();

        Assert.True(ability.Can("read", new WorkItem { Status = WorkStatus.Open }));
        Assert.False(ability.Can("read", new WorkItem { Status = WorkStatus.Done }));
    }

    public void Dispose() => _connection.Dispose();
}
