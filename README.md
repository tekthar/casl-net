# Msar.Authz

**CASL-compatible authorization for .NET.** Write permission rules once, in the [CASL](https://casl.js.org) format, and use them three ways:

- **In memory:** `ability.Can("update", workItem)`
- **In the database:** `db.WorkItems.AccessibleBy(ability, "read")`. Rules become a LINQ expression that EF Core translates to SQL, with rule values sent as SQL parameters.
- **In the browser:** send `ability.ToCaslRules()` to the frontend and check them with the real `@casl/ability`.

All three give the same answers. That isn't a promise; two tests check it on every build:
- **Database vs memory:** 400 random rule sets × 2 actions × 120 entities on a real EF Core database. The SQL filter and the in-memory check return the same rows every time.
- **CASL compatibility:** 24,000 decisions on 150 random rule sets, made by Msar.Authz and by `@casl/ability` 7, all identical.

Msar.Authz is part of [Msar](https://github.com/msar-dev), an open-source work management platform, but it has no dependency on Msar, ABP or anything else beyond .NET. Use it in any .NET 8 or later application.

## Quick start

```bash
dotnet add package Msar.Authz --prerelease
```

```csharp
// 1. Say which types are subjects, and which members rules may filter on.
var subjects = new SubjectRegistry()
    .Add<WorkItem>("WorkItem", s => s
        .ConditionFields("projectId", "assigneeId", "status", "priority", "labels",
                         "project.orgUnitId", "comments.authorId")
        .Fields("summary", "status", "assigneeId", "priority"));

// 2. Build one user's ability from rules: from code, from your database, from role grants.
var ability = new AbilityBuilder(subjects)
    .WithVariables(new { user = new { id = currentUserId } })
    .Can("read", "WorkItem", new { projectId = new Dictionary<string, object> { ["$in"] = new[] { 1, 2 } } })
    .AddRange(RawRule.ParseMany(rulesJsonFromDatabase), variables: new { grant = new { projectIds = new[] { 7 } } })
    .Cannot("update", "WorkItem", new { status = "Done" }, reason: "Done items are frozen", "status")
    .Build();

// 3. Use it.
ability.Can("update", item);                       // one object
ability.Can("update", item, "status");             // one field
ability.PermittedFields("update", item);           // ["summary", "assigneeId", "priority"]
ability.EnsureCan("update", item, "status");       // throws ForbiddenException("Done items are frozen")
var visible = await db.WorkItems.AccessibleBy(ability, "read").ToListAsync();   // filtered in SQL
var forBrowser = ability.ToCaslRules();            // JSON for @casl/ability
```

Rules are plain JSON in CASL's format, so they can live in a database table and be edited by admins:

```json
{ "action": "update", "subject": "WorkItem",
  "conditions": { "projectId": { "$in": "${grant.projectIds}" }, "assigneeId": "${user.id}" },
  "fields": ["summary", "status"] }
```

In the browser:

```js
import { createMsarAbility } from '@msar/authz-casl';
import { subject } from '@casl/ability';

const ability = createMsarAbility(rulesFromServer);
ability.can('update', subject('WorkItem', item));
```

## Conditions

MongoDB query syntax, the same subset CASL supports, plus logical operators:

| Kind | Operators |
|---|---|
| Comparison | `$eq` (or a plain value), `$ne`, `$gt`, `$gte`, `$lt`, `$lte`, `$in`, `$nin`, `$exists` |
| Logical | `$and`, `$or`, `$nor`, `$not` |
| Arrays | `{ labels: "bug" }` means "labels contains bug"; also `$all`, `$elemMatch`, and dot notation through collections (`"comments.authorId": "u2"` means "any comment by u2") |
| Paths | Dot notation through navigations: `"project.orgUnitId"` |
| Placeholders | `"${user.id}"`, `"${grant.projectIds}"`; a whole-string placeholder can be an array or a number |

How they behave:
- **Safe by default.** Only member paths registered with `ConditionFields` are accepted, so rule authors can't filter on `PasswordHash`. Unknown operators, unknown fields, unknown placeholders and values of the wrong type fail when the ability is built. They are never skipped, because skipping a "cannot" rule would widen access.
- **Values become SQL parameters**, so EF Core caches one query plan for every user, not one per user.
- **Missing related objects read as null**, the way a LEFT JOIN does: `{ "project.archived": false }` doesn't match an item with no project.
- **Ordering operators aren't allowed on text, `Guid`, `bool` or enums**, because databases and .NET order text differently.

## Rule semantics

- **Deny overrides.** Access is granted when at least one allow rule matches and no "cannot" rule matches, whatever order the rules come in. CASL instead lets later rules win; `ToCaslRules()` puts deny rules last so CASL reaches the same answers.
- **`manage` and `all`** mean every action and every subject, as in CASL.
- **Field-level rules follow CASL.** A check without a field ignores "cannot" rules limited to fields and counts allow rules limited to fields. A field-limited "cannot" doesn't hide rows from `AccessibleBy`; it only restricts those fields.
- **`Can(action, "Subject")` without an instance** answers "could this user do it to some instance?", for menus and buttons.
- **Validate rules before saving them:** `AbilityBuilder.Validate(rule, subjects)` checks paths, operators and fields without needing placeholder values.

## Repository

```
src/Msar.Authz/             the library (net8.0, net10.0)
packages/authz-casl/        @msar/authz-casl, the browser companion
tests/Msar.Authz.Tests/     unit tests and the database-vs-memory equivalence test (EF Core + SQLite)
tests/casl-compat/          the CASL compatibility test (Node, real @casl/ability)
```

```bash
dotnet test
cd tests/casl-compat && npm install && npm test   # after dotnet test, which writes the fixture
```

## Status

Preview (0.1.0-preview.1). The API may change before 1.0. Planned next:
- custom operators, for example `$under` for org-unit subtrees through a closure table
- ordering operators on enums
- an ASP.NET Core package for per-request abilities and authorization policies

## Credits and licence

Apache License 2.0. Msar.Authz is an independent implementation of CASL's rule format and semantics. CASL ([stalniy/casl](https://github.com/stalniy/casl)) is MIT-licensed, and its design is what this library follows. No CASL code is included in the .NET library.
