using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace Casl.Tests;

/// <summary>
/// Very wide rules and very many rules run in SQL and agree with the in-memory check. Combined as left-deep chains,
/// they overflowed the stack while EF Core translated the query, which ends the whole process.
/// </summary>
public sealed class PostgresRuleSizeTests
{
    private static async Task<(List<int> Sql, List<int> Memory)> Compare(string database, Ability ability)
    {
        DbContextOptions<TestDb> options = Postgres.Fresh(database);
        await using (TestDb seed = new(options))
        {
            seed.WorkItems.AddRange(Enumerable.Range(1, 6).Select(i => new WorkItem { Id = i, Summary = $"item {i}", Priority = i }));
            await seed.SaveChangesAsync();
        }

        await using TestDb db = new(options);
        List<int> sql = await db.WorkItems.AccessibleBy(ability, "read").Select(w => w.Id).OrderBy(id => id).ToListAsync();
        List<WorkItem> all = await db.WorkItems.AsNoTracking().ToListAsync();
        return (sql, all.Where(w => ability.Can("read", w)).Select(w => w.Id).OrderBy(id => id).ToList());
    }

    [PostgresFact]
    public async Task A_rule_with_twenty_thousand_alternatives()
    {
        RawRule rule = new()
        {
            Action = ["read"],
            Subject = ["WorkItem"],
            Conditions = new JsonObject
            {
                ["$or"] = new JsonArray(Enumerable.Range(1000, 19_999).Select(p => (JsonNode?)new JsonObject { ["priority"] = p })
                    .Append(new JsonObject { ["priority"] = 3 }).ToArray()),
            },
        };

        (List<int> sql, List<int> memory) = await Compare("casl_rule_size_wide", new AbilityBuilder(Subjects.Create()).Add(rule).Build());

        Assert.Equal([3], sql);
        Assert.Equal(sql, memory);
    }

    [PostgresFact]
    public async Task Ten_thousand_rules_under_both_semantics()
    {
        foreach (RuleSemantics semantics in new[] { RuleSemantics.Casl, RuleSemantics.DenyOverrides })
        {
            AbilityBuilder builder = new(Subjects.Create(), new AbilityOptions { Semantics = semantics });
            builder.AddRange(Enumerable.Range(1000, 10_000).Select(p => RawRule.Can("read", "WorkItem", new { priority = p })));
            builder.Can("read", "WorkItem", new { priority = 2 });
            builder.Can("read", "WorkItem", new { priority = 5 });
            builder.Cannot("read", "WorkItem", new { priority = 5 });

            (List<int> sql, List<int> memory) = await Compare("casl_rule_size_many", builder.Build());

            Assert.Equal([2], sql);
            Assert.Equal(sql, memory);
        }
    }
}
