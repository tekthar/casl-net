using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Casl.Tests;

/// <summary>
/// The SQLite equivalence test, on PostgreSQL through Npgsql: the rows <c>AccessibleBy</c> returns must be exactly the
/// objects <c>Can</c> accepts. SQLite's REGEXP runs .NET's engine, so only a real PostgreSQL shows where the two differ.
/// </summary>
public sealed class PostgresEquivalenceTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task Database_filter_and_in_memory_check_always_agree()
    {
        DbContextOptions<TestDb> options = Postgres.Fresh("casl_equivalence");
        Random random = new(20261006);

        await using (TestDb seed = new(options))
        {
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
            AbilityOptions abilityOptions = new() { Semantics = i % 2 == 0 ? RuleSemantics.Casl : RuleSemantics.DenyOverrides };
            Ability ability = new AbilityBuilder(registry, abilityOptions).AddRange(rules).Build();
            foreach (string action in new[] { "read", "update" })
            {
                List<int> fromDatabase = await db.WorkItems.AccessibleBy(ability, action).Select(w => w.Id).OrderBy(id => id).ToListAsync();
                List<int> inMemory = all.Where(w => ability.Can(action, w)).Select(w => w.Id).OrderBy(id => id).ToList();
                if (!fromDatabase.SequenceEqual(inMemory))
                {
                    output.WriteLine($"{ability.Options.Semantics}: {JsonSerializer.Serialize(rules)}");
                }

                Assert.Equal(inMemory, fromDatabase);
                compared++;
            }
        }

        output.WriteLine($"{compared} rule set and action pairs agreed across {all.Count} work items on PostgreSQL.");
    }

    // Texts that tell regular expression engines apart: newlines, non-ASCII digits, letters and spaces, case folding.
    private static readonly string[] Texts =
    [
        "", "a", "abc", "ABC", "abc\n", "a\nb", "\nabc", "x abc y", "xabcy", "a.c", "a-c", "a_c", "a1c", "a٣c", "aéc",
        "ÉCOLE", "école", "a c", "a\tc", "a confidential plan", "confidential", "ab", "aab", "abab", "a[b", "a]b",
        "a{2}", "aa", "a\\b", "abc#x", "ⅰⅱ", "İstanbul", "istanbul", "K", "a c", "a c",
        "a\u0085c", "a\u000bc", "a\fc", "a\rc", "a\r\nb", "b\n", "\n", "a\nb\n",
    ];

    // Everything the library accepts, including the constructs it rewrites ($ and [^...]).
    private static readonly string[] Patterns =
    [
        "abc", "^abc", "abc$", "^abc$", "a.c", "a.b", "a|b", "(ab)+", "(?:ab)+", "a*b", "a+b", "a?b", "a{2}", "a{2,}", "a{1,2}b",
        "a*?b", "[abc]", "[a-c]", "[^abc]", "x[^a]y", "a[^x]b", "^[^]a]$", "[^-a]", "x[^\\]]y", "(?:a|[^b])+$", "[]a]", "\\.",
        "\\*", "\\[", "\\]", "a\\-c", "\\^", "\\$", "\\\\", "\\t", "\\n", "\\u0061", "[a\\]b]", "[a\\-c]", "[.]", "[$]",
        "(?=a)a", "(?!b)a", "(?<=a)b", "(?<!a)b", "a{", "{a}", "a b", "a#x", ".", "^$", "^b", "b$", "^a$", "abc$|^x",
        "a  b # comment $ [^",
    ];

    private static readonly string[] OptionSets = ["", "i", "m", "s", "x", "im", "is", "ix", "mx", "sx", "imx", "isx"];

    [PostgresFact]
    public async Task Regex_decides_the_same_in_memory_and_in_postgres()
    {
        DbContextOptions<TestDb> options = Postgres.Fresh("casl_regex");
        List<WorkItem> items = Texts.Select((text, i) => new WorkItem { Id = i + 1, Summary = text }).ToList();
        await using (TestDb seed = new(options))
        {
            seed.WorkItems.AddRange(items.Select(w => new WorkItem { Id = w.Id, Summary = w.Summary }));
            await seed.SaveChangesAsync();
        }

        await using TestDb db = new(options);
        SubjectRegistry registry = Subjects.Create();
        int compared = 0;
        List<string> mismatches = [];
        foreach (string pattern in Patterns)
        {
            foreach (string flags in OptionSets)
            {
                foreach (bool inverted in new[] { false, true })
                {
                    JsonObject regex = new() { ["$regex"] = pattern, ["$options"] = flags };
                    AbilityBuilder builder = new(registry);
                    if (inverted)
                    {
                        builder.Can("read", "WorkItem");
                    }

                    builder.Add(new RawRule { Action = ["read"], Subject = ["WorkItem"], Conditions = new JsonObject { ["summary"] = regex }, Inverted = inverted });
                    Ability ability;
                    try
                    {
                        ability = builder.Build();
                    }
                    catch (CaslConfigurationException)
                    {
                        continue; // not a valid .NET pattern under these options
                    }

                    List<int> fromDatabase = await db.WorkItems.AccessibleBy(ability).Select(w => w.Id).OrderBy(id => id).ToListAsync();
                    List<int> inMemory = items.Where(w => ability.Can("read", w)).Select(w => w.Id).ToList();
                    if (!fromDatabase.SequenceEqual(inMemory))
                    {
                        mismatches.Add($"{pattern} /{flags}/ inverted={inverted}: database only {Show(fromDatabase.Except(inMemory))}, memory only {Show(inMemory.Except(fromDatabase))}");
                    }

                    compared++;
                }
            }
        }

        foreach (string mismatch in mismatches)
        {
            output.WriteLine(mismatch);
        }

        output.WriteLine($"{compared} regex rules compared over {Texts.Length} texts.");
        Assert.Empty(mismatches);
        Assert.True(compared > 1000);

        string Show(IEnumerable<int> ids) => JsonSerializer.Serialize(ids.Select(id => Texts[id - 1]));
    }
}
