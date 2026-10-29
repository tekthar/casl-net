# Changelog

All notable changes to CASL .NET (`Casl.Net` on NuGet) and its browser companion (`casl-net` on npm). Versions follow
[Semantic Versioning](https://semver.org); before 1.0, a minor version (0.2, 0.3) may change the API, and this file
says how.

## Unreleased

**Security**
- A placeholder whose variable is null now fails the build, as a missing one already did. Before, a null variable was
  filled in as JSON null (a whole placeholder) or as empty text (inside a longer string), so `{ "assigneeId":
  "${user.id}" }` with a null id matched every unassigned record, in memory and in SQL, and a `cannot` rule written the
  same way stopped denying. Abilities built with null variables now throw `CaslConfigurationException`.
- `ToMongoQuery` now follows `RuleSemantics.DenyOverrides`. It used CASL's "last rule wins" whatever the semantics, so
  a `cannot` rule added before an allow rule was left out: `Cannot(read, Post, { secret: true })` then
  `Can(read, Post)` gave `{}` (every document) instead of `{ "$nor": [{ "secret": true }] }`, unlike `Can` and
  `Filter`.
- `PermittedFields` now leaves out fields that a `cannot` rule's pattern denies. It removed a deny's field strings
  literally, so `Cannot(read, WorkItem, fields: "cost.*")` removed nothing and `cost.total` stayed listed while
  `Can(read, item, "cost.total")` returned false.
- `$regex` now decides the same in memory and on PostgreSQL. The pattern ran as .NET syntax in memory but as
  PostgreSQL syntax in SQL, so `{ "$regex": "\\bconfidential\\b" }` in a `cannot` rule denied in `Can` but matched
  nothing in `AccessibleBy` (`\b` is a backspace in PostgreSQL), which returned the rows `Can` refused.
  - **Breaking:** constructs PostgreSQL reads differently now fail the build: `\d \D \w \W \s \S \b \B \A \Z \z \G`,
    `\p{…}`, `\x…`, back-references and octal escapes, named, atomic and conditional groups, inline options, POSIX
    classes and class subtraction, and the options `m` and `s` together. See
    [Conditions](docs/conditions.md#text) for replacements.
  - Without `m`, `$` no longer matches before a final newline, and unless `s` is set, `[^…]` no longer matches a
    newline, in memory and on SQLite too, as on PostgreSQL.
  - A pattern is now validated with its `$options`, so one that is valid only without them fails the build.
- Placeholder text inside a `$regex` pattern is now matched literally. It was filled in as raw text, so a variable
  such as a self-chosen slug `x|.*` turned `"^/teams/${user.slug}/"` into a pattern matching every value. A pattern
  that is exactly one placeholder is still used as it is.
- In-memory `$regex` matches stop after `AbilityOptions.RegexMatchTimeout` (100 ms by default) and throw
  `RegexMatchTimeoutException`, so a costly pattern from a rule author can't hold a request thread. Field patterns
  (`**.**.zz`) match in linear time.
- With a strict registry, `$elemMatch` on a path the registry doesn't list now fails with the same "not allowed"
  message as any other condition. It used to say whether the member existed and what type it had.
- A rule whose `conditions` isn't an object (a string, an array, a number) or whose subject list is empty now fails.
  The first used to be dropped, making the rule unconditional, and the second became a rule on every subject.

**Added**
- `AbilityOptions.RegexMatchTimeout`.
- `tests/Casl.Net.PostgresTests` and the optional `postgres` workflow: the equivalence test and `$regex` comparisons on
  a real PostgreSQL, run when `CASL_POSTGRES` holds a connection string.

## 0.1.0 (2026-10-10)

The first release.

**Rules and checks**
- CASL's rule format (`action`, `subject`, `conditions`, `fields`, `inverted`, `reason`), from code with
  `AbilityBuilder` or from JSON with `RawRule`.
- Checks on objects, subject types, CLR types, tagged objects (`Subject.Of`) and claims: `Can`, `Cannot`,
  `EnsureCan`, `UnlessCan`, with `ForbiddenException` carrying the action, subject, field and reason.
- Two ways rules combine: CASL's "last rule wins" (the default) and deny-overrides
  (`RuleSemantics.DenyOverrides`), for rules assembled from several grants.
- `manage` and `all`, action aliases (`ActionAliases`), custom subject type detection.
- Placeholders in conditions (`${user.id}`), from builder-wide and per-rule variables; a missing value fails the build.
- Rule lookup (`RulesFor`, `PossibleRulesFor`, `RelevantRuleFor`, `ActionsFor`), `PermittedFields`, `DefaultValues`.
- `AbilityBuilder.Validate` checks a rule before it's saved.

**Conditions**
- MongoDB query syntax, CASL's subset: `$eq`, `$ne`, `$gt`, `$gte`, `$lt`, `$lte`, `$in`, `$nin`, `$exists`, `$regex`
  with `$options`, `$and`, `$or`, `$nor`, `$not`, `$all`, `$size`, `$elemMatch`; paths through navigations,
  collections and array positions.
- Rules that can't be enforced exactly (unknown subjects, paths, operators, fields or placeholders, values of the wrong
  type, ordering on text, `Guid`, `bool` or enums) fail the build instead of being skipped.

**Subjects and fields**
- Strict registries (`SubjectRegistry`) list the member paths conditions may use and the fields rules may name; open
  registries behave like CASL.
- Field-level rules with CASL's field patterns; field families (`fields.**`) for fields declared at run time;
  `FieldMatch.Named` for fields only rules that name them may grant.

**Queries**
- `AccessibleBy` and `Filter<T>`: rules as a LINQ predicate that EF Core translates to SQL, with every rule value a
  SQL parameter. The same rows as the in-memory check, verified on every build.
- `ToMongoQuery` for document stores, and `Ability.RulesToCondition` for anything else.

**The browser**
- `ToCaslRules()` exports rules for `@casl/ability`, with aliases expanded, deny-overrides ordering and conditions
  adjusted where CASL's matcher departs from MongoDB.
- `PackedRules`, compatible with CASL's `packRules` and `unpackRules`.
- `casl-net` 0.1.0 on npm: `createAbility` configures `@casl/ability` (6.7 or 7) with the logical operators.

**Compatibility**
- 172 of CASL's 187 runtime tests, ported to C#; the other 15 test JavaScript-only behaviour.
- .NET 8 and .NET 10. No dependencies.
