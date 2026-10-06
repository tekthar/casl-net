using System.Text.Json.Nodes;

namespace Msar.Authz.Tests;

/// <summary>Deterministic random work items and rule sets for the equivalence and CASL compatibility tests.</summary>
internal static class RandomRules
{
    private static readonly string[] Users = ["u1", "u2", "u3"];
    private static readonly string[] LabelPool = ["bug", "ui", "blocked", "backend"];
    private static readonly string[] Actions = ["read", "update"];

    public static List<Project> Projects() =>
    [
        new() { Id = 1, Name = "Alpha", OrgUnitId = 1, Archived = false },
        new() { Id = 2, Name = "Beta", OrgUnitId = 2, Archived = false },
        new() { Id = 3, Name = "Gamma", OrgUnitId = 3, Archived = true },
    ];

    public static List<WorkItem> WorkItems(Random random, int count)
    {
        List<WorkItem> items = [];
        int commentId = 1;
        for (int i = 1; i <= count; i++)
        {
            int? projectId = random.Next(4) is var p && p == 0 ? null : p;
            WorkItem item = new()
            {
                Id = i,
                Summary = $"Item {i}",
                ProjectId = projectId,
                AssigneeId = random.Next(4) == 0 ? null : Users[random.Next(Users.Length)],
                Status = (WorkStatus)random.Next(3),
                Priority = random.Next(1, 6),
                DueAt = random.Next(3) == 0 ? null : new DateTime(2026, random.Next(1, 13), random.Next(1, 28), 0, 0, 0, DateTimeKind.Utc),
                Labels = LabelPool.Where(_ => random.Next(3) == 0).ToList(),
            };
            for (int c = random.Next(3); c > 0; c--)
            {
                item.Comments.Add(new Comment { Id = commentId++, AuthorId = Users[random.Next(Users.Length)], Internal = random.Next(2) == 0 });
            }

            items.Add(item);
        }

        return items;
    }

    public static List<RawRule> RuleSet(Random random)
    {
        List<RawRule> rules = [];
        for (int i = random.Next(1, 5); i > 0; i--)
        {
            bool inverted = rules.Count > 0 && random.Next(3) == 0;
            JsonObject? conditions = random.Next(6) == 0 ? null : Query(random, depth: 0);
            IReadOnlyList<string>? fields = random.Next(5) == 0 ? ["status"] : null;
            rules.Add(new RawRule { Action = [Actions[random.Next(Actions.Length)]], Subject = ["WorkItem"], Conditions = conditions, Fields = fields, Inverted = inverted });
        }

        return rules;
    }

    private static JsonObject Query(Random random, int depth)
    {
        int pick = random.Next(depth < 1 ? 20 : 17);
        string user = Users[random.Next(Users.Length)];
        string label = LabelPool[random.Next(LabelPool.Length)];
        return pick switch
        {
            0 => Obj("priority", new JsonObject { ["$gte"] = random.Next(1, 6) }),
            1 => Obj("priority", new JsonObject { ["$lt"] = random.Next(1, 6) }),
            2 => Obj("status", ((WorkStatus)random.Next(3)).ToString()),
            3 => Obj("status", new JsonObject { ["$in"] = new JsonArray("Open", "InProgress") }),
            4 => Obj("status", new JsonObject { ["$ne"] = "Done" }),
            5 => Obj("assigneeId", user),
            6 => Obj("assigneeId", null),
            7 => Obj("assigneeId", new JsonObject { ["$exists"] = random.Next(2) == 0 }),
            8 => Obj("assigneeId", new JsonObject { ["$nin"] = new JsonArray(user, "u9") }),
            9 => Obj("projectId", new JsonObject { ["$in"] = new JsonArray(1, 2) }),
            10 => Obj("project.orgUnitId", random.Next(1, 4)),
            11 => Obj("project.archived", false),
            12 => Obj("labels", label),
            13 => Obj("labels", new JsonObject { ["$all"] = new JsonArray("bug", "ui") }),
            14 => Obj("labels", new JsonObject { ["$nin"] = new JsonArray(label) }),
            15 => Obj("comments", new JsonObject { ["$elemMatch"] = new JsonObject { ["authorId"] = user, ["internal"] = random.Next(2) == 0 } }),
            16 => Obj("dueAt", new JsonObject { ["$lt"] = "2026-06-01T00:00:00Z" }),
            17 => new JsonObject { ["$or"] = new JsonArray(Query(random, depth + 1), Query(random, depth + 1)) },
            18 => new JsonObject { ["$nor"] = new JsonArray(Query(random, depth + 1)) },
            _ => Merge(Query(random, depth + 1), Query(random, depth + 1)),
        };
    }

    private static JsonObject Obj(string key, JsonNode? value) => new() { [key] = value };

    private static JsonObject Merge(JsonObject a, JsonObject b)
    {
        // Two queries on the same key can't share one object; nest them under $and instead.
        if (a.Any(p => b.ContainsKey(p.Key)))
        {
            return new JsonObject { ["$and"] = new JsonArray(a, b) };
        }

        foreach ((string key, JsonNode? value) in b.ToList())
        {
            b.Remove(key);
            a[key] = value;
        }

        return a;
    }
}
