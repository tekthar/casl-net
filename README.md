# CASL .NET

[![NuGet](https://img.shields.io/nuget/v/Casl.Net.svg)](https://www.nuget.org/packages/Casl.Net)
[![npm](https://img.shields.io/npm/v/casl-net.svg)](https://www.npmjs.com/package/casl-net)
[![CI](https://github.com/tekthar/casl-net/actions/workflows/ci.yml/badge.svg)](https://github.com/tekthar/casl-net/actions/workflows/ci.yml)
[![License: Apache-2.0](https://img.shields.io/badge/license-Apache--2.0-blue.svg)](https://github.com/tekthar/casl-net/blob/main/LICENSE)

**Permissions you can read out loud.**

CASL .NET (say it like *castle dot net*) is a small authorization library for .NET that answers one question: **may this
user do this to that?** You write rules that read like your business requirements. Then you ask them in memory, in your
database query, or in the browser, and you get the same answer in all three.

It handles both simple role checks and full attribute-based access control (ABAC), without a policy language to
learn or a server to run.

```csharp
var ability = new AbilityBuilder()
    .Can("read", "BlogPost")
    .Can("manage", "BlogPost", new { authorId = user.Id })
    .Cannot("delete", "BlogPost", new { published = true }).Because("Published posts stay published")
    .Build();

ability.Can("update", post);                     // in memory
db.BlogPosts.AccessibleBy(ability, "update");    // in SQL, through EF Core
ability.ToCaslRules();                           // in the browser, with @casl/ability
```

> An independent, community-built .NET implementation of [CASL](https://casl.js.org)'s rule format and semantics. Not
> affiliated with or endorsed by CASL or its author.

## Why you'll like it

- **Readable**: a rule is an action, a subject, and optionally some conditions and fields. Anyone on the team can
  read it, including the product manager.
- **Start small, grow later**: begin with `Can("login")`, add roles, and add attribute conditions only once you need
  them. You don't have to rewrite anything as you go.
- **Write once, check anywhere**: the same rules check one object in memory, filter whole tables in SQL, and hide
  buttons in the browser.
- **Rules are data**: rules are plain JSON in CASL's format, so you can keep them in a table, let admins edit them,
  and send them to the frontend.
- **Safe by default**: a rule that can't be enforced exactly fails when the ability is built. It is never skipped.
  You choose which fields a rule may filter on, so nobody can write a rule against `PasswordHash`.
- **No dependencies**: works with .NET 8 and later, with or without EF Core.
- **Tested against CASL itself**: CASL's own test suite, ported to C#, runs on every build. So does a comparison with
  the real `@casl/ability`. [More below](#how-do-we-know-it-works).

If you've used CASL in JavaScript, you already know how this works.

## Install

```bash
dotnet add package Casl.Net
```

For the browser:

```bash
npm install casl-net @casl/ability
```

Everything lives in one namespace: `using Casl;`.

## Crash course

An ability is built from **rules**, and every rule has up to four parts:

1. **Action**: what the user does. Any verb your app needs: `read`, `update`, `publish`, `refund`.
   `manage` means every action.
2. **Subject**: what they do it to: `BlogPost`, `Invoice`, `User`. `all` means every subject.
3. **Conditions** *(optional)*: which instances the rule applies to, such as "posts they wrote". Conditions use
   MongoDB-style queries like `{ authorId: 42 }` or `{ status: { $in: [...] } }`.
4. **Fields** *(optional)*: which properties the rule covers, such as "may edit `title`, but not `published`".

Rules are either `Can` or `Cannot`. Let's build some.

### 1. Define what users can do

Say a blog has these requirements:

- anyone can read posts;
- authors can do anything with their own posts;
- nobody can delete a published post.

Here they are as rules:

```csharp
using Casl;

Ability DefineAbilityFor(User user) => new AbilityBuilder()
    .Can("read", "BlogPost")
    .Can("manage", "BlogPost", new { authorId = user.Id })
    .Cannot("delete", "BlogPost", new { published = true }).Because("Published posts stay published")
    .Build();
```

Each requirement became one line. Conditions use your property names in camelCase: `authorId` reads `AuthorId`.

### 2. Check them

```csharp
var ability = DefineAbilityFor(currentUser);

ability.Can("read", "BlogPost");     // true: can they read at least some posts?
ability.Can("update", myDraft);      // true: they wrote it
ability.Can("update", someoneElses); // false
ability.Can("delete", myDraft);      // true
ability.Can("delete", myLivePost);   // false: it's published

ability.EnsureCan("delete", myLivePost);
// throws ForbiddenException: "Published posts stay published"
```

An object's subject type is its class name, so a `BlogPost` instance is checked against `"BlogPost"` rules. You can also
use the type itself: `.Can<BlogPost>("read")`.

### 3. Filter the database

Checking one object at a time doesn't work for a list page. `AccessibleBy` turns the same rules into a LINQ filter,
and EF Core translates it to SQL:

```csharp
var deletable = await db.BlogPosts
    .AccessibleBy(ability, "delete")
    .ToListAsync();
```

```sql
SELECT ... FROM "BlogPosts" AS "b"
WHERE "b"."AuthorId" = @Value AND "b"."Published" <> @Value1
```

The query only returns rows the user may delete. Rule values are sent as SQL parameters, so every user shares one
cached query plan. It's an ordinary `IQueryable`, so you can chain `Where`, `OrderBy` and paging before or after it.

### 4. Share rules with the browser

Send the rules to the frontend, and the UI hides exactly what the server would refuse:

```csharp
app.MapGet("/api/me/rules", () => DefineAbilityFor(currentUser).ToCaslRules());
```

```js
import { createAbility } from 'casl-net';
import { subject } from '@casl/ability';

const ability = createAbility(await fetch('/api/me/rules').then(r => r.json()));

ability.can('delete', subject('BlogPost', post));   // same answer as the server
```

This is the real [`@casl/ability`](https://casl.js.org), so CASL's React, Vue and Angular integrations work with it
too.

### 5. Let admins write the rules

When rules come from a database or an admin screen, register your subjects first. A strict registry only accepts
rules on the members you list:

```csharp
var subjects = new SubjectRegistry()
    .Add<BlogPost>("BlogPost", s => s
        .ConditionFields("authorId", "published")   // what rules may filter on
        .Fields("title", "body"));                  // what field-level rules may name
```

Rules are JSON, with placeholders that are filled in for each user:

```json
[
  { "action": "update", "subject": "BlogPost",
    "conditions": { "authorId": "${user.id}" },
    "fields": ["title", "body"] }
]
```

```csharp
var ability = new AbilityBuilder(subjects)
    .WithVariables(new { user = new { id = currentUser.Id } })
    .AddRange(RawRule.ParseMany(rulesFromDatabase))
    .Build();

ability.Can("update", post, "title");          // true for their own posts
ability.PermittedFields("update", post);       // ["title", "body"]
```

A rule that filters on an unregistered member is rejected with a clear error:

```text
CaslConfigurationException: Conditions on 'BlogPost.passwordHash' are not allowed.
Register the path with ConditionFields("passwordHash").
```

To catch mistakes before a rule is saved, check it first with `AbilityBuilder.Validate(rule, subjects)`.

## Conditions at a glance

Conditions use the same MongoDB subset as CASL:

| | |
|---|---|
| Compare | `$eq` (or a plain value), `$ne`, `$gt`, `$gte`, `$lt`, `$lte`, `$in`, `$nin`, `$exists` |
| Combine | `$and`, `$or`, `$nor`, `$not` |
| Text | `$regex` with `$options` |
| Arrays | `{ labels: "bug" }` means "contains", plus `$all`, `$size`, `$elemMatch` |
| Paths | `"project.orgUnitId"`, `"comments.authorId"` (any comment), `"authors.0"` |
| Placeholders | `"${user.id}"`, `"${grant.projectIds}"` |

The [conditions guide](https://github.com/tekthar/casl-net/blob/main/docs/conditions.md) covers every operator, and
what is refused and why.

## Everything else CASL has

CASL .NET follows CASL closely, so its other features are here too: "last rule wins" semantics (or **deny-overrides**,
when rules come from several roles), action aliases, field patterns like `author.*`, claim rules, custom subject
detection, rule lookup (`RulesFor`, `RelevantRuleFor`), `ToMongoQuery`, and packed rules for a smaller payload.

[Coming from CASL](https://github.com/tekthar/casl-net/blob/main/docs/casl-spec-port.md) maps each CASL function to its
.NET name.

## How do we know it works?

Authorization is not a good place for surprises, so three test suites check the in-memory, SQL and browser answers
against each other on every build:

- **CASL's own test suite**, ported to C#: 172 of CASL's 187 runtime tests. The other 15 test JavaScript-only
  behaviour ([details](https://github.com/tekthar/casl-net/blob/main/docs/casl-spec-port.md)).
- **Database vs memory**: 400 random rule sets × 2 actions × 120 entities on a real EF Core database, under both rule
  semantics. The SQL filter and the in-memory check return the same rows every time.
- **CASL compatibility**: 24,000 decisions on 150 random rule sets, made by CASL .NET and by `@casl/ability` 7. All of
  them are identical.

## Packages

| Package | | What it is |
|---|---|---|
| [`Casl.Net`](https://www.nuget.org/packages/Casl.Net) | [![NuGet](https://img.shields.io/nuget/v/Casl.Net.svg)](https://www.nuget.org/packages/Casl.Net) | The library: abilities, rules, EF Core filtering. .NET 8+, no dependencies |
| [`casl-net`](https://www.npmjs.com/package/casl-net) | [![npm](https://img.shields.io/npm/v/casl-net.svg)](https://www.npmjs.com/package/casl-net) | The browser companion: configures `@casl/ability` to read CASL .NET rules |

## Documentation

| Guide | What it covers |
|---|---|
| [Getting started](https://github.com/tekthar/casl-net/blob/main/docs/getting-started.md) | A first ability, rules from a database, one ability per request in ASP.NET Core |
| [Rules](https://github.com/tekthar/casl-net/blob/main/docs/rules.md) | The rule format, placeholders, how rules combine, validating rules before saving them |
| [Conditions](https://github.com/tekthar/casl-net/blob/main/docs/conditions.md) | Every operator, with examples |
| [Subjects and fields](https://github.com/tekthar/casl-net/blob/main/docs/subjects-and-fields.md) | Strict and open registries, condition paths, field-level rules and patterns |
| [EF Core and SQL](https://github.com/tekthar/casl-net/blob/main/docs/ef-core.md) | `AccessibleBy` and `Filter<T>`, the generated SQL, navigations, nulls, other stores |
| [The browser](https://github.com/tekthar/casl-net/blob/main/docs/browser.md) | Sending rules to `@casl/ability`, packed rules, field checks in the UI |
| [Coming from CASL](https://github.com/tekthar/casl-net/blob/main/docs/casl-spec-port.md) | Each CASL function's .NET name, and which of CASL's tests are ported |

The API reference ships as XML documentation, so your editor shows it as you type. The
[changelog](https://github.com/tekthar/casl-net/blob/main/CHANGELOG.md) lists what each release changed.

## Status and roadmap

This is **0.1.0**, the first release. Every build checks its decisions against CASL and a real database, but the API
may still change before 1.0. Minor versions may include breaking changes, and the
[changelog](https://github.com/tekthar/casl-net/blob/main/CHANGELOG.md) will describe them. Coming next:

- custom operators, for example `$under` for org-unit subtrees
- ordering operators on enums
- an ASP.NET Core package for per-request abilities and authorization policies

## Have a question? Want to help?

Questions, ideas and bug reports are welcome as [issues](https://github.com/tekthar/casl-net/issues). If you'd like to
contribute code or docs, read the [contributing guide](https://github.com/tekthar/casl-net/blob/main/CONTRIBUTING.md).
Please report security problems through [private reporting](https://github.com/tekthar/casl-net/blob/main/SECURITY.md),
not in public issues.

```
src/Casl.Net/               the library (package Casl.Net, namespace Casl; net8.0, net10.0)
packages/casl-net/          casl-net on npm, the browser companion
tests/Casl.Net.Tests/       unit tests, CASL's ported specs (CaslSpec/) and the database-vs-memory test
tests/casl-compat/          the CASL compatibility test (Node, real @casl/ability)
```

To run the tests:

```bash
dotnet test
```

```bash
cd tests/casl-compat && npm install && npm test   # after dotnet test, which writes the fixture
```

CASL .NET was first built for Msar, an open-source work management platform. [Tekthar](https://github.com/tekthar)
maintains it as a standalone project.

## Credits and licence

Apache License 2.0.

CASL .NET is an independent implementation of the rule format and semantics of CASL
([stalniy/casl](https://github.com/stalniy/casl), MIT, by Sergii Stotskyi). The design is CASL's. Thank you. A few
files are translations of CASL code, and the CASL tests are ported from CASL's specs. These are listed, with CASL's
licence, in [THIRD-PARTY-NOTICES.md](https://github.com/tekthar/casl-net/blob/main/THIRD-PARTY-NOTICES.md).

**Sister project:** [abp-mcp](https://github.com/tekthar/abp-mcp), which turns your ABP Framework app into an MCP server
that AI agents can call with the permissions your users already have.
