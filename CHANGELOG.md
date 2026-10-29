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
