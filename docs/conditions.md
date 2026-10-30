# Conditions

Conditions limit a rule to the instances that match a query in MongoDB syntax, the same subset CASL's MongoDB matcher
supports, with the logical operators switched on. Every condition is checked in memory, translated to SQL by
`AccessibleBy`, and exported to `@casl/ability` with the same meaning.

Paths are member names in camelCase: `authorId` reads `AuthorId`. Dots walk into navigations (`project.orgUnitId`),
through collections (`comments.authorId`) and into array positions (`authors.0`).

## Equality

```json
{ "status": "Open" }                      // status == "Open"
{ "status": { "$eq": "Open" } }           // the same
{ "status": { "$ne": "Done" } }           // anything but Done, including no value
{ "dueAt": null }                         // no due date
{ "status": { "$in": ["Open", "Blocked"] } }
{ "status": { "$nin": ["Done"] } }
{ "dueAt": { "$exists": true } }          // has a value
```

`$in` and `$nin` need an array, or a placeholder that fills one in (`{ "$in": "${user.teamIds}" }`).

## Ordering

```json
{ "priority": { "$gte": 3 } }
{ "dueAt": { "$lt": "2026-11-01T00:00:00Z" } }
```

`$gt`, `$gte`, `$lt` and `$lte` work on numbers, dates and times. They're refused on text, `Guid`, `bool` and enums,
because .NET and databases order those differently, and the answer must not depend on where the check runs. A
comparison with `null` never matches, as in MongoDB.

## Text

```json
{ "summary": { "$regex": "^urgent", "$options": "i" } }
```

`$regex` takes a regular expression and works on text members only. `$options` may contain `i`, `m`, `s` and `x`,
but not `m` and `s` together. An invalid pattern fails the build. In SQL, it becomes `Regex.IsMatch`, which EF Core
translates on providers with regular expressions (PostgreSQL and SQLite, for example); on others the query fails to
translate instead of running in memory.

The pattern must mean the same in .NET and in the database, so constructs PostgreSQL reads differently, or not at
all, fail the build:

| Refused | Use instead |
|---|---|
| `\d` `\D` `\w` `\W` `\s` `\S` (they differ on non-ASCII text) | `[0-9]`, `[A-Za-z0-9_]`, `[ \t\n\r\f\v]` |
| `\b` `\B` (a backspace in PostgreSQL) | spell out the boundary, such as `(^\|[^A-Za-z])word($\|[^A-Za-z])` |
| `\A` `\Z` `\z` `\G` `\p{…}` `\P{…}` `\x…`, back-references and octal (`\1`, `\141`) | `^`, `$`, the characters themselves |
| named, atomic and conditional groups, inline options, `(?#…)` comments | `(…)`, `(?:…)`, `$options` |
| `[` inside a character class (POSIX classes such as `[[:alpha:]]`, class subtraction) | `\[` for a literal, or list the characters |

Everything else works as usual: literals, `.`, `^`, `$`, `|`, `(…)`, `(?:…)`, lookarounds, quantifiers, character
classes and escaped symbols such as `\.` or `\[`, plus `\t`, `\n`, `\r`, `\f`, `\v` and `\uXXXX`. Two things follow
PostgreSQL's rules, in memory as well: without `m`, `$` matches only at the very end of the text, not before a final
newline; and unless `s` is set, `[^…]` doesn't match a newline (just as `.` doesn't).

Rule authors choose the pattern, so a match in memory stops after `AbilityOptions.RegexMatchTimeout` (100 ms by
default) and the check throws `RegexMatchTimeoutException` instead of deciding. In SQL the database's own limits
apply; on SQLite, EF Core's `REGEXP` runs .NET's engine in your process, bounded only by the process-wide
`REGEX_DEFAULT_MATCH_TIMEOUT` setting.

## Logic

```json
{ "$or": [ { "assigneeId": "${user.id}" }, { "reporterId": "${user.id}" } ] }
{ "$and": [ { "status": "Open" }, { "priority": { "$gte": 3 } } ] }
{ "$nor": [ { "status": "Done" }, { "archived": true } ] }
{ "priority": { "$not": { "$gte": 4 } } }
```

Keys side by side are combined with AND. `$and`, `$or` and `$nor` take a non-empty array of queries; `$not` takes an
operator object. Other top-level operators (`$where`, `$expr` and the like) are refused.

## Collections

A path that ends on a collection checks its elements; a path that passes through one means "any element":

```json
{ "labels": "bug" }                                  // labels contains "bug"
{ "labels": { "$all": ["bug", "ui"] } }              // contains both
{ "labels": { "$size": 0 } }                         // no labels
{ "comments.authorId": "${user.id}" }                // any comment written by the user
{ "comments": { "$elemMatch": { "authorId": "${user.id}", "internal": false } } }   // one comment matching both
{ "authors.0": "${user.id}" }                        // the first author
```

`$elemMatch` needs every condition to hold for the same element; separate paths through a collection may each match a
different element, as in MongoDB. `$size` takes a non-negative whole number.

## Nulls and missing objects

A related object that is missing reads as null, the way a LEFT JOIN does: `{ "project.archived": false }` doesn't match
an item with no project, in memory or in SQL.

## Values and types

Rule values are converted to the member's type when the ability is built, and a value that can't be read as that type
fails the build:

| Member type | Rule value |
|---|---|
| numbers (`int`, `long`, `decimal`, `double`…) | a number, or text that parses as one (`3`, `"3"`) |
| `string` | text; a number or `true` becomes its JSON text |
| `bool` | `true` or `false` |
| enums | the member's name, in any letter case (`"open"`), or its number |
| `Guid` | its text form |
| `DateTime`, `DateTimeOffset`, `DateOnly` | ISO 8601 text (`"2026-11-01T00:00:00Z"`, `"2026-11-01"`) |

Members of other types can't be used in conditions.

## What's refused

| | Why |
|---|---|
| A whole object as a value, `{ "project": { "id": 1 } }` | Ambiguous: use dotted paths, `{ "project.id": 1 }` |
| An exact array as a value, `{ "labels": ["a", "b"] }` | Ambiguous: use `$all` or `$in` |
| An unknown operator | Skipping it would change the rule's meaning |
| A path the [strict registry](subjects-and-fields.md) doesn't list | Rule authors can only filter on what you allow |
| A placeholder without a value | Dropping it would widen or narrow access |

All of these throw `CaslConfigurationException` when the ability is built, or show up in `AbilityBuilder.Validate`.
