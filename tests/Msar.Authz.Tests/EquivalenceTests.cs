using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Msar.Authz.Tests;

/// <summary>
/// The test that keeps this library honest: for hundreds of random rule sets, the rows EF Core returns
/// for <c>Filter&lt;T&gt;</c> must be exactly the objects <c>Can</c> accepts in memory.
/// </summary>
public sealed class EquivalenceTests(ITestOutputHelper output) : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    [Fact]
    public async Task Database_filter_and_in_memory_check_always_agree()
    {
        _connection.Open();
        DbContextOptions<TestDb> options = new DbContextOptionsBuilder<TestDb>().UseSqlite(_connection).Options;
        Random random = new(20261006);

        await using (TestDb seed = new(options))
        {
            await seed.Database.EnsureCreatedAsync();
            seed.Projects.AddRange(RandomRules.Projects());
            seed.WorkItems.AddRange(RandomRules.WorkItems(random, 120));
            await seed.SaveChangesAsync();
        }

        await using TestDb db = new(options);
        List<WorkItem> all = await db.WorkItems.Include(w => w.Project).Include(w => w.Comments).AsNoTracking().ToListAsync();
        SubjectRegistry registry = Subjects.Create();
        int compared = 0;

        for (int i = 0; i < 400; i++)
        {
            List<RawRule> rules = RandomRules.RuleSet(random);
            Ability ability = new AbilityBuilder(registry).AddRange(rules).Build();
            foreach (string action in new[] { "read", "update" })
            {
                List<int> fromDatabase = await db.WorkItems.AccessibleBy(ability, action).Select(w => w.Id).OrderBy(id => id).ToListAsync();
                List<int> inMemory = all.Where(w => ability.Can(action, w)).Select(w => w.Id).OrderBy(id => id).ToList();
                if (!fromDatabase.SequenceEqual(inMemory))
                {
                    output.WriteLine(JsonSerializer.Serialize(rules));
                    output.WriteLine($"database only: {string.Join(',', fromDatabase.Except(inMemory))}; memory only: {string.Join(',', inMemory.Except(fromDatabase))}");
                }

                Assert.Equal(inMemory, fromDatabase);
                compared++;
            }
        }

        output.WriteLine($"{compared} rule set and action pairs agreed across {all.Count} work items.");
    }

    /// <summary>
    /// Writes cases for tests/casl-compat, where the real @casl/ability must reach the same decisions
    /// from <see cref="Ability.ToCaslRules"/>.
    /// </summary>
    [Fact]
    public void Write_casl_compatibility_fixture()
    {
        Random random = new(7);
        List<Project> projects = RandomRules.Projects();
        List<WorkItem> items = RandomRules.WorkItems(random, 40);
        foreach (WorkItem item in items)
        {
            item.Project = projects.FirstOrDefault(p => p.Id == item.ProjectId);
        }

        SubjectRegistry registry = Subjects.Create();
        JsonSerializerOptions json = new(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, // MongoDB: a missing field, not a null one
            Converters = { new JsonStringEnumConverter(), new UtcDateConverter() },
        };

        JsonArray cases = [];
        for (int i = 0; i < 150; i++)
        {
            Ability ability = new AbilityBuilder(registry).AddRange(RandomRules.RuleSet(random)).Build();
            JsonArray checks = [];
            for (int index = 0; index < items.Count; index++)
            {
                foreach (string action in new[] { "read", "update" })
                {
                    checks.Add(new JsonObject { ["action"] = action, ["item"] = index, ["expected"] = ability.Can(action, items[index]) });
                    checks.Add(new JsonObject { ["action"] = action, ["item"] = index, ["field"] = "status", ["expected"] = ability.Can(action, items[index], "status") });
                }
            }

            cases.Add(new JsonObject
            {
                ["rules"] = JsonSerializer.SerializeToNode(ability.ToCaslRules()),
                ["checks"] = checks,
            });
        }

        JsonObject fixture = new()
        {
            ["items"] = JsonSerializer.SerializeToNode(items, json),
            ["cases"] = cases,
        };

        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "casl-compat", "fixture.json"));
        File.WriteAllText(path, fixture.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        output.WriteLine($"Wrote {cases.Count} cases to {path}");
    }

    public void Dispose() => _connection.Dispose();

    private sealed class UtcDateConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetDateTime();

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"));
    }
}
