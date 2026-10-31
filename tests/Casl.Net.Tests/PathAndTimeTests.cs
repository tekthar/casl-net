using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Casl.Tests;

/// <summary>Index paths and time values must mean the same in memory and in SQL, on every server.</summary>
public sealed class PathAndTimeTests : IDisposable
{
    private static readonly SubjectRegistry Registry = new SubjectRegistry()
        .Add<WorkItem>("WorkItem", s => s.ConditionFields("comments.0.authorId", "labels.0", "dueAt"));

    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    private async Task<TestDb> SeededDb()
    {
        _connection.Open();
        TestDb db = new(new DbContextOptionsBuilder<TestDb>().UseSqlite(_connection).Options);
        await db.Database.EnsureCreatedAsync();
        db.WorkItems.AddRange(
            new WorkItem { Id = 1, Summary = "a", Labels = ["bug", "ui"], DueAt = new DateTime(2026, 10, 31, 21, 0, 0, DateTimeKind.Utc) },
            new WorkItem { Id = 2, Summary = "b", Labels = ["ui", "bug"], DueAt = new DateTime(2026, 11, 1, 3, 0, 0, DateTimeKind.Utc) },
            new WorkItem { Id = 3, Summary = "c", Labels = [] });
        await db.SaveChangesAsync();
        return db;
    }

    // Related rows have no order in SQL: EF Core turns the index into LIMIT/OFFSET without an ORDER BY, so after an
    // ordinary UPDATE the database could pick a different "first" comment than the list in memory.
    [Fact]
    public void An_index_into_a_collection_of_objects_is_refused_in_sql_but_works_in_memory()
    {
        Ability ability = new AbilityBuilder(Registry).Can("read", "WorkItem", new Dictionary<string, object> { ["comments.0.authorId"] = "u2" }).Build();

        CaslConfigurationException error = Assert.Throws<CaslConfigurationException>(() => ability.Filter<WorkItem>("read"));
        Assert.Contains("comments.0.authorId", error.Message);

        Assert.True(ability.Can("read", new WorkItem { Comments = [new() { AuthorId = "u2" }, new() { AuthorId = "u1" }] }));
        Assert.False(ability.Can("read", new WorkItem { Comments = [new() { AuthorId = "u1" }, new() { AuthorId = "u2" }] }));
    }

    [Fact]
    public void A_cannot_rule_with_such_an_index_is_refused_in_sql_too()
    {
        Ability ability = new AbilityBuilder(Registry)
            .Can("read", "WorkItem")
            .Cannot("read", "WorkItem", new Dictionary<string, object> { ["comments.0.authorId"] = "u1" })
            .Build();

        Assert.Throws<CaslConfigurationException>(() => ability.Filter<WorkItem>("read"));
    }

    [Fact]
    public async Task An_index_into_a_collection_of_values_works_in_sql()
    {
        Ability ability = new AbilityBuilder(Registry).Can("read", "WorkItem", new Dictionary<string, object> { ["labels.0"] = "bug" }).Build();

        await using TestDb db = await SeededDb();
        List<int> sql = await db.WorkItems.AccessibleBy(ability, "read").Select(w => w.Id).OrderBy(id => id).ToListAsync();
        List<int> memory = (await db.WorkItems.AsNoTracking().ToListAsync()).Where(w => ability.Can("read", w)).Select(w => w.Id).ToList();

        Assert.Equal([1], sql);
        Assert.Equal(sql, memory);
    }

    // Text with an offset names an instant. Read as the server's local time (Kind=Local), the rule moved by the
    // server's UTC offset; Kind=Utc also lets Npgsql send it to a timestamptz column.
    [Theory]
    [InlineData("2026-11-01T00:00:00+00:00", "2026-11-01T00:00:00")]
    [InlineData("2026-11-01T05:00:00+05:00", "2026-11-01T00:00:00")]
    [InlineData("2026-10-31T17:00:00-07:00", "2026-11-01T00:00:00")]
    [InlineData("2026-11-01T00:00:00Z", "2026-11-01T00:00:00")]
    public void Time_with_an_offset_is_the_same_instant_on_every_server(string text, string utc)
    {
        foreach (JsonNode value in new[] { JsonNode.Parse($"\"{text}\"")!, JsonValue.Create(text) })
        {
            DateTime converted = (DateTime)ValueConverter.Convert(value, typeof(DateTime?), "dueAt")!;

            Assert.Equal(DateTimeKind.Utc, converted.Kind);
            Assert.Equal(DateTime.Parse(utc, System.Globalization.CultureInfo.InvariantCulture), new DateTime(converted.Ticks));
        }
    }

    [Fact]
    public void Time_without_an_offset_is_used_as_written()
    {
        DateTime converted = (DateTime)ValueConverter.Convert(JsonValue.Create("2026-11-01T00:00:00"), typeof(DateTime), "dueAt")!;

        Assert.Equal(DateTimeKind.Unspecified, converted.Kind);
        Assert.Equal(new DateTime(2026, 11, 1), converted);
    }

    [Fact]
    public void A_DateTimeOffset_value_without_an_offset_is_utc()
    {
        DateTimeOffset converted = (DateTimeOffset)ValueConverter.Convert(JsonValue.Create("2026-11-01T00:00:00"), typeof(DateTimeOffset), "at")!;

        Assert.Equal(TimeSpan.Zero, converted.Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), converted);
    }

    [Fact]
    public async Task A_rule_filled_from_a_DateTimeOffset_variable_expires_on_time()
    {
        Ability ability = new AbilityBuilder(Registry)
            .WithVariables(new { now = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero) })
            .Can("read", "WorkItem", new { dueAt = new Dictionary<string, object> { ["$gt"] = "${now}" } })
            .Build();

        await using TestDb db = await SeededDb();
        List<int> sql = await db.WorkItems.AccessibleBy(ability, "read").Select(w => w.Id).OrderBy(id => id).ToListAsync();
        List<int> memory = (await db.WorkItems.AsNoTracking().ToListAsync()).Where(w => ability.Can("read", w)).Select(w => w.Id).ToList();

        Assert.Equal([2], sql); // item 1 was due at 21:00 UTC the day before
        Assert.Equal(sql, memory);
    }

    public void Dispose() => _connection.Dispose();
}
