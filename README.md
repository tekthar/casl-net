# CASL .NET

> An independent, community-built .NET implementation of [CASL](https://casl.js.org)'s rule format and semantics. Not affiliated with or endorsed by CASL or its author.

**CASL-compatible authorization for .NET.** Write permission rules once, in the [CASL](https://casl.js.org) format, and use them three ways:

- **In memory:** `ability.Can("update", workItem)`
- **In the database:** `db.WorkItems.AccessibleBy(ability, "read")`. Rules become a LINQ expression that EF Core translates to SQL, with rule values sent as SQL parameters.
- **In the browser:** send `ability.ToCaslRules()` to the frontend and check them with the real `@casl/ability`.

All three give the same answers. That isn't a promise; three sets of tests check it on every build:
- **CASL's own test suite**, ported to C#: 172 of CASL's 187 runtime tests; the other 15 test JavaScript-only behaviour ([details](docs/casl-spec-port.md)).
- **Database vs memory:** 400 random rule sets × 2 actions × 120 entities on a real EF Core database, under both rule semantics. The SQL filter and the in-memory check return the same rows every time.
- **CASL compatibility:** 24,000 decisions on 150 random rule sets, made by CASL .NET and by `@casl/ability` 7, all identical.

It has no dependencies beyond .NET itself, so you can use it in any .NET 8 or later application, with or without EF Core. It was first built for [Msar](https://github.com/msar-dev), an open-source work management platform, and is maintained as a standalone project.

## Quick start

Everything lives in the `Casl` namespace: `using Casl;`.

```bash
dotnet add package Casl.Net --prerelease
```

If you know CASL, it works the way you expect:

```csharp
var ability = new AbilityBuilder()
    .Can("read", "Post")
    .Can("update", "Post", new { authorId = user.Id })
    .Cannot("delete", "Post", new { published = true }).Because("Published posts can't be deleted")
    .Build();

ability.Can("update", post);          // the subject type of a Post object is "Post"
ability.Can("delete", "Post");        // "on at least some posts?"
```

When rules come from users (an admin screen, a database), use a strict registry, so rules can only filter on
members you list:

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
import { createAbility } from 'casl-net';
import { subject } from '@casl/ability';

const ability = createAbility(rulesFromServer);
ability.can('update', subject('WorkItem', item));
```

## Conditions

MongoDB query syntax, the same subset CASL supports, plus logical operators:

| Kind | Operators |
|---|---|
| Comparison | `$eq` (or a plain value), `$ne`, `$gt`, `$gte`, `$lt`, `$lte`, `$in`, `$nin`, `$exists` |
| Text | `$regex`, with `$options` (`i`, `m`, `s`, `x`) |
| Logical | `$and`, `$or`, `$nor`, `$not` |
| Arrays | `{ labels: "bug" }` means "labels contains bug"; also `$all`, `$size`, `$elemMatch`, and dot notation through collections (`"comments.authorId": "u2"` means "any comment by u2") |
| Paths | Dot notation through navigations (`"project.orgUnitId"`) and array indexes (`"authors.0"`) |
| Placeholders | `"${user.id}"`, `"${grant.projectIds}"`; a whole-string placeholder can be an array or a number |

How they behave:
- **Safe by default with a strict registry.** Only member paths registered with `ConditionFields` are accepted, so rule authors can't filter on `PasswordHash`. Unknown operators, unknown fields, unknown placeholders and values of the wrong type fail when the ability is built. They are never skipped, because skipping a "cannot" rule would widen access.
- **Values become SQL parameters**, so EF Core caches one query plan for every user, not one per user.
- **Missing related objects read as null**, the way a LEFT JOIN does: `{ "project.archived": false }` doesn't match an item with no project.
- **Ordering operators aren't allowed on text, `Guid`, `bool` or enums**, because databases and .NET order text differently.

## Rule semantics

- **CASL's by default: the last matching rule wins.** `Can("read", "Post")` followed by `Cannot("read", "Post", new { secret = true })` hides secret posts; in the other order, the later `Can` lets them through.
- **Or deny-overrides**, with `new AbilityOptions { Semantics = RuleSemantics.DenyOverrides }`: access is granted when an allow rule matches and no "cannot" rule matches, whatever order the rules come in. Choose this when rules are combined from several roles or grants, where order means nothing. `ToCaslRules()` then puts "cannot" rules last, so `@casl/ability` reaches the same answers.
- **`manage` and `all`** mean every action and every subject, as in CASL. Both can be renamed (`AnyAction`, `AnySubjectType`).
- **Field-level rules follow CASL.** A check without a field ignores "cannot" rules limited to fields and counts allow rules limited to fields. A field-limited "cannot" doesn't hide rows from `AccessibleBy`; it only restricts those fields.
- **`Can(action, "Subject")` without an instance** answers "could this user do it to some instance?", for menus and buttons.
- **Validate rules before saving them:** `AbilityBuilder.Validate(rule, subjects)` checks paths, operators and fields without needing placeholder values.
- **Abilities are immutable.** Build a new one when rules change. One ability can be shared across threads, and it caches its compiled checks and SQL filters.

## Everything else CASL has

| | |
|---|---|
| Action aliases | `new AbilityOptions { Aliases = ActionAliases.Create(new Dictionary<string, string[]> { ["modify"] = ["update", "delete"] }) }` |
| Field patterns | `.OnFields("author.*")`, `"author.**"`, `"*.name"`, `"street*"` |
| Claim rules | `Can("login")`, checked with `ability.Can("login")` |
| Class subjects | `Can<Post>("read")`; `ability.Can("read", typeof(Post))` |
| Tagged objects | `ability.Can("read", Subject.Of("Article", dto))` |
| Custom subject detection | `AbilityOptions.DetectSubjectType` |
| Error messages | `.Because("…")`, `EnsureCan(…, message: …)`, `AbilityOptions.ForbiddenMessage`; `ForbiddenException` carries `Action`, `Subject`, `SubjectType`, `Field`, `Reason` |
| Rule lookup | `RulesFor`, `PossibleRulesFor`, `RelevantRuleFor`, `ActionsFor` |
| Fields and defaults | `PermittedFields` (`permittedFieldsOf`), `DefaultValues` (`rulesToFields`) |
| Queries | `Filter<T>` and `AccessibleBy` for EF Core; `ToMongoQuery` for document stores; `Ability.RulesToCondition` for anything else |
| Compact transport | `PackedRules.Pack` / `Unpack`, compatible with CASL's `packRules` / `unpackRules` |

[docs/casl-spec-port.md](docs/casl-spec-port.md) maps each CASL function to its .NET name.

## Repository

```
src/Casl.Net/               the library (package Casl.Net, namespace Casl; net8.0, net10.0)
packages/casl-net/          casl-net on npm, the browser companion
tests/Casl.Net.Tests/       unit tests, CASL's ported specs (CaslSpec/) and the database-vs-memory test (EF Core + SQLite)
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

Apache License 2.0. CASL .NET is an independent implementation of CASL's rule format and semantics. CASL ([stalniy/casl](https://github.com/stalniy/casl)) is MIT-licensed, and its design is what this library follows. A few files are translations of CASL code (alias resolution, field patterns, rule packing, `rulesToCondition`), and the CASL tests are ported from CASL's specs; they are listed, with CASL's licence, in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
