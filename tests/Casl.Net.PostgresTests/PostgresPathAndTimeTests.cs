using Microsoft.EntityFrameworkCore;

namespace Casl.Tests;

/// <summary>Index paths and time values decide the same in memory and on PostgreSQL.</summary>
public sealed class PostgresPathAndTimeTests
{
    private static readonly SubjectRegistry Registry = new SubjectRegistry()
        .Add<WorkItem>("WorkItem", s => s.ConditionFields("comments.0.authorId", "labels.0", "dueAt"));

    private static async Task<(List<int> Sql, List<int> Memory)> Compare(string database, Ability ability)
    {
        DbContextOptions<TestDb> options = Postgres.Fresh(database);
        await using (TestDb seed = new(options))
        {
            seed.WorkItems.AddRange(
                new WorkItem { Id = 1, Summary = "a", Labels = ["bug", "ui"], DueAt = new DateTime(2026, 10, 31, 21, 0, 0, DateTimeKind.Utc) },
                new WorkItem { Id = 2, Summary = "b", Labels = ["ui", "bug"], DueAt = new DateTime(2026, 11, 1, 3, 0, 0, DateTimeKind.Utc) },
                new WorkItem { Id = 3, Summary = "c", Labels = [] });
            await seed.SaveChangesAsync();
        }

        await using TestDb db = new(options);
        List<int> sql = await db.WorkItems.AccessibleBy(ability, "read").Select(w => w.Id).OrderBy(id => id).ToListAsync();
        List<WorkItem> all = await db.WorkItems.AsNoTracking().ToListAsync();
        return (sql, all.Where(w => ability.Can("read", w)).Select(w => w.Id).OrderBy(id => id).ToList());
    }

    [PostgresFact]
    public async Task An_index_into_a_collection_of_values()
    {
        Ability ability = new AbilityBuilder(Registry).Can("read", "WorkItem", new Dictionary<string, object> { ["labels.0"] = "bug" }).Build();

        (List<int> sql, List<int> memory) = await Compare("casl_index_values", ability);

        Assert.Equal([1], sql);
        Assert.Equal(sql, memory);
    }

    // A DateTimeOffset variable becomes text with an offset. Read as local time it moved the rule by the server's UTC
    // offset, and Npgsql refused the Kind=Local value for a timestamptz column.
    [PostgresFact]
    public async Task A_rule_filled_from_a_DateTimeOffset_variable()
    {
        Ability ability = new AbilityBuilder(Registry)
            .WithVariables(new { now = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero) })
            .Can("read", "WorkItem", new { dueAt = new Dictionary<string, object> { ["$gt"] = "${now}" } })
            .Build();

        (List<int> sql, List<int> memory) = await Compare("casl_time_offset", ability);

        Assert.Equal([2], sql);
        Assert.Equal(sql, memory);
    }
}
