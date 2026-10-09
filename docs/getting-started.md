# Getting started

CASL .NET answers one question: may this person do this action to this thing? You describe what a person may do as
rules, build an `Ability` from them, and ask it, in memory, in the database, or in the browser.

## Install

```bash
dotnet add package Casl.Net
```

It targets .NET 8 and .NET 10 and has no dependencies. Everything is in the `Casl` namespace.

## A first ability

```csharp
using Casl;

var ability = new AbilityBuilder()
    .Can("read", "Post")
    .Can("update", "Post", new { authorId = userId })
    .Cannot("delete", "Post", new { published = true }).Because("Published posts can't be deleted")
    .Build();

ability.Can("read", post);              // true
ability.Can("update", post);            // true when post.AuthorId == userId
ability.Can("delete", post);            // false when post.Published
ability.EnsureCan("delete", post);      // throws ForbiddenException("Published posts can't be deleted")
```

`new AbilityBuilder()` uses an **open** registry, as CASL does: any subject name is accepted, an object's subject type
is its class name (`Post`), and conditions may use any public member, written in camelCase (`authorId` reads
`AuthorId`). That's fine when developers write the rules.

## Rules from a database

When rules come from an admin screen or a table, register your subjects in a **strict** registry. Rules may then only
filter on the members you list, and only name the fields you list. Anything else fails when the ability is built, so a
rule can't filter on `PasswordHash` and a typo can't quietly grant nothing or everything.

```csharp
// Once, at startup.
var subjects = new SubjectRegistry()
    .Add<WorkItem>("WorkItem", s => s
        .ConditionFields("projectId", "assigneeId", "status", "project.orgUnitId")
        .Fields("summary", "status", "assigneeId", "priority"));
```

Rules are JSON in CASL's format. Placeholders such as `${user.id}` are filled in when the ability is built:

```json
[
  { "action": "read", "subject": "WorkItem", "conditions": { "projectId": { "$in": "${grant.projectIds}" } } },
  { "action": "update", "subject": "WorkItem", "conditions": { "assigneeId": "${user.id}" }, "fields": ["summary", "status"] }
]
```

```csharp
// Per person: their own variables, and each grant's rules with that grant's scope.
var builder = new AbilityBuilder(subjects).WithVariables(new { user = new { id = userId } });
foreach (var grant in grantsOf(userId))
{
    builder.AddRange(RawRule.ParseMany(grant.RulesJson), variables: new { grant = new { projectIds = grant.ProjectIds } });
}

Ability ability = builder.Build();
```

Before saving a rule an admin wrote, check it: `AbilityBuilder.Validate(rule, subjects)` returns the problems found
(an empty list means it's valid) without needing the placeholder values.

## In the database

`AccessibleBy` turns the rules into a LINQ predicate, which EF Core translates to SQL:

```csharp
var mine = await db.WorkItems.AccessibleBy(ability, "read").ToListAsync();
```

Rule values become SQL parameters, so one query plan serves every user. See [EF Core and SQL](ef-core.md).

## In the browser

Send `ability.ToCaslRules()` to the frontend and check the same rules with `@casl/ability`, through the `casl-net`
npm package. See [The browser](browser.md).

## One ability per request in ASP.NET Core

An `Ability` is immutable and thread-safe, and it caches what it compiles (checks and SQL predicates). Build one per
person and keep it while their rules don't change. A simple shape:

```csharp
public interface IAbilityProvider
{
    Task<Ability> ForUserAsync(Guid userId, CancellationToken ct = default);
}

public sealed class AbilityProvider(SubjectRegistry subjects, IGrantStore grants, IMemoryCache cache) : IAbilityProvider
{
    public async Task<Ability> ForUserAsync(Guid userId, CancellationToken ct = default) =>
        (await cache.GetOrCreateAsync(("ability", userId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5); // or evict when grants change
            var builder = new AbilityBuilder(subjects).WithVariables(new { user = new { id = userId } });
            foreach (Grant grant in await grants.ForUserAsync(userId, ct))
            {
                builder.AddRange(RawRule.ParseMany(grant.RulesJson), new { grant = new { projectIds = grant.ProjectIds } });
            }

            return builder.Build();
        }))!;
}
```

Register the registry as a singleton and the provider as you like; check in controllers or endpoints with
`ability.EnsureCan(...)` and map `ForbiddenException` to a 403.

When grants come from several roles, their order means nothing. Build with
`new AbilityOptions { Semantics = RuleSemantics.DenyOverrides }` so any matching `cannot` wins, whatever the order.
See [Rules](rules.md#how-rules-combine).

## Next

- [Rules](rules.md): the format, placeholders, how rules combine
- [Conditions](conditions.md): every operator
- [Subjects and fields](subjects-and-fields.md): registries and field-level rules
