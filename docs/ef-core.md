# EF Core and SQL

CASL .NET turns rules into a LINQ predicate. EF Core translates it to SQL, so the database returns only the rows a
person may act on, with paging, sorting and counting done there too.

## Filtering a query

```csharp
var visible = await db.WorkItems
    .AccessibleBy(ability, "read")          // the rows the ability allows for "read"
    .Where(x => x.ProjectId == projectId)    // your own filters narrow it further
    .OrderBy(x => x.CreatedAt)
    .Take(50)
    .ToListAsync();
```

`AccessibleBy(ability, action)` is `query.Where(ability.Filter<T>(action))`. `Filter<T>` returns the
`Expression<Func<T, bool>>` itself, for when you need to combine it yourself:

```csharp
Expression<Func<WorkItem, bool>> canRead = ability.Filter<WorkItem>("read");
var counts = await db.Projects
    .Select(p => new { p.Id, Open = p.Items.AsQueryable().Where(canRead).Count() })
    .ToListAsync();
```

The subject type is the one registered for `T` (or `T`'s class name in an open registry). When nothing is allowed, the
predicate is `false` and the database returns nothing; when everything is, it's `true`.

## What the SQL looks like

For

```csharp
new AbilityBuilder(subjects)
    .Can("read", "WorkItem", new { projectId = new Dictionary<string, object> { ["$in"] = new[] { 1, 2 } } })
    .Can("read", "WorkItem", new { assigneeId = userId })
    .Cannot("read", "WorkItem", new { status = "Archived" })
    .Build();
```

the predicate is, in effect, `(projectId IN @p0 OR assigneeId = @p1) AND NOT (status = @p2)`.

- **Values are parameters.** Every rule value is sent as a SQL parameter, never inlined. EF Core caches one query plan
  for every user, and rule values can't inject SQL.
- **Each `Filter<T>` is built once per ability and action**, then reused; build the ability once per person (see
  [Getting started](getting-started.md#one-ability-per-request-in-aspnet-core)).
- **Navigations become joins**: `project.orgUnitId` reads `x.Project.OrgUnitId`. A missing related row reads as null,
  as in a LEFT JOIN, and in memory a missing object reads as null too, so both give the same answer.
- **Collections become `EXISTS`**: `comments.authorId` is `x.Comments.Any(c => c.AuthorId == @p)`; `labels: "bug"`
  is `x.Labels.Contains(@p)`; `$size` counts.
- **Rule semantics carry over.** With CASL's semantics each allow rule is bounded by the `cannot` rules added after it;
  with deny-overrides it's `(any allow) AND NOT (any cannot)`.
- **`cannot` rules limited to fields don't filter rows.** Hiding a field doesn't hide the row; check fields when you
  shape the response (`ability.Can("read", item, "cost")`, or `PermittedFields`).

## Providers

The library only builds LINQ expressions, so it works with any provider that translates them. Its own tests run the
rules on SQLite through EF Core: 400 random rule sets, two actions each, against 120 entities, under both semantics,
compare every SQL result with the in-memory check. The same test, plus a `$regex` comparison over texts that tell
regular expression engines apart, runs on PostgreSQL through Npgsql in the `postgres` workflow
(`tests/Casl.Net.PostgresTests`).

Two things depend on the provider:
- **`$regex`** becomes `Regex.IsMatch`, which PostgreSQL and SQLite translate and some providers don't. A predicate EF
  Core can't translate makes the query throw; it never falls back to filtering in memory. Patterns are limited to
  what .NET and PostgreSQL read the same way (see [Conditions](conditions.md#text)), so `Can` and the SQL filter agree
  on both.
- **Text comparisons** follow the database's collation for `$eq` and `$in`. Ordering operators are refused on text for
  that reason (see [Conditions](conditions.md#ordering)).

## Other stores

- **`ability.ToMongoQuery(action, subjectType)`** gives the rules as one MongoDB query: with CASL semantics an `$or` of
  allow rules, each with `$nor` of the `cannot` rules above it; with deny-overrides the `$or` of allow rules and a
  `$nor` of every `cannot` rule. It returns null when nothing is allowed and `{}` when everything is. Use it for
  document databases and search engines.
- **`Ability.RulesToCondition(rules, convert, and, or, empty)`** is CASL's `rulesToCondition`: give it
  `ability.RulesFor(action, subjectType)` and three functions that build your own query type.
