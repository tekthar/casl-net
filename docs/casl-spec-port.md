# CASL spec port

CASL .NET runs CASL's own test suite, translated to C#. The source is `packages/casl-ability/spec/` in
[stalniy/casl](https://github.com/stalniy/casl) at commit `e2b885a`, MIT-licensed (see `THIRD-PARTY-NOTICES.md`).
The ported tests are in `tests/Casl.Net.Tests/CaslSpec/`, one file per spec file, with test names kept close
to CASL's.

Of CASL's 187 runtime tests, 172 are ported and 15 don't apply to .NET. The TypeScript type tests
(`spec/types/`) check TypeScript typings and have no .NET equivalent.

| CASL spec | Tests | Ported | Adapted | Not applicable | .NET file |
|---|---:|---:|---:|---:|---|
| `ability.spec.ts` | 84 | 68 | 4 | 12 | `AbilitySpec.cs` |
| `builder.spec.ts` | 14 | 10 | 1 | 3 | `BuilderSpec.cs` |
| `error.spec.ts` | 7 | 5 | 2 | 0 | `ErrorSpec.cs` |
| `pack_rules.spec.ts` | 25 | 25 | 0 | 0 | `PackRulesSpec.cs` |
| `permitted_fields.spec.ts` | 7 | 7 | 0 | 0 | `PermittedFieldsSpec.cs` |
| `rulesToAST.spec.ts` | 21 | 0 | 21 | 0 | `RulesToAstSpec.cs` |
| `rulesToCondition.spec.ts` | 18 | 18 | 0 | 0 | `RulesToConditionSpec.cs` |
| `rulesToFields.spec.ts` | 7 | 7 | 0 | 0 | `RulesToFieldsSpec.cs` |
| `subject_helper.spec.ts` | 4 | 4 | 0 | 0 | `SubjectHelperSpec.cs` |
| **Total** | **187** | **144** | **28** | **15** | |

"Adapted" means the test checks the same behaviour through the .NET API, where CASL's test relies on something
JavaScript-specific. Tests marked "Not in CASL's spec" in the files are additions.

## API mapping

| CASL | CASL .NET |
|---|---|
| `defineAbility((can, cannot) => …)` | `new AbilityBuilder().Can(…).Cannot(…).Build()` |
| `createMongoAbility(rules, options)` | `new AbilityBuilder(options).AddRange(rules).Build()` |
| `can(action, subject, fields, conditions)` | `Can(action, subject, conditions, params fields)`, or `.Where(…)` and `.OnFields(…)` |
| `cannot(…).because(reason)` | `Cannot(…, reason: …)` or `.Because(reason)` |
| `can('read', Post)` (class subject) | `Can<Post>("read")`; checks take `typeof(Post)` |
| `createAliasResolver(map)` / `resolveAction` | `ActionAliases.Create(map)` / `AbilityOptions.Aliases` |
| `anyAction`, `anySubjectType`, `detectSubjectType` | the `AbilityOptions` properties of the same names |
| `ability.rules`, `rulesFor`, `possibleRulesFor`, `relevantRuleFor`, `actionsFor` | `Rules`, `RulesFor`, `PossibleRulesFor`, `RelevantRuleFor`, `ActionsFor` |
| `ForbiddenError.from(ability).throwUnlessCan(…)` | `ability.EnsureCan(…)` |
| `ForbiddenError#unlessCan`, `setMessage` | `ability.UnlessCan(…)`, `EnsureCan(…, message: …)` |
| `ForbiddenError.setDefaultMessage(fn)` | `AbilityOptions.ForbiddenMessage` (per ability, not global) |
| `subject('Article', object)` | `Subject.Of("Article", value)` |
| `packRules`, `unpackRules` | `PackedRules.Pack`, `PackedRules.Unpack` |
| `permittedFieldsOf(ability, action, subject, { fieldsFrom })` | `ability.PermittedFields(action, subject, fieldsFrom)` |
| `rulesToFields(ability, action, subjectType)` | `ability.DefaultValues(action, subjectType)` |
| `rulesToCondition(rules, convert, hooks)` | `Ability.RulesToCondition(rules, convert, and, or, empty)` |
| `rulesToAST(ability, action, subjectType)` | `ability.Filter<T>(action)` (a LINQ expression) or `ability.ToMongoQuery(action, subjectType)` |

## Not applicable

`ability.spec.ts`:
- **`update` and its events** (8 tests: "allows to update rules", "triggers update event", "triggers updated
  event", and the five subscription tests). A CASL .NET ability is immutable: build a new one when rules change.
  That makes an ability safe to share across threads and lets it cache compiled checks and SQL filters.
- **"properly compares object-primitives like `ObjectId` that have `toJSON` method"**. JavaScript-specific;
  .NET compares values by type.
- **"throws exception if 3rd argument is not a string"**. The compiler enforces it.
- **"throws … if 'fieldMatcher' option was not provided"** and **"… 'conditionsMatcher' option …"**. CASL .NET
  always matches conditions and fields; there is nothing to configure.

`builder.spec.ts`:
- **"can create Ability instance from a factory function"** and **"… from a factory arrow function"**. There is
  one ability type, so there is no factory to pass.
- **"returns `Promise<Ability>` when used async DSL"**. Build rules asynchronously, then call `Build()`.

## Adapted

- `ability.spec.ts`
  - **`$in` and `$all`**: CASL's tests put a string or an array in the same `state` property. .NET needs two
    properties, so arrays use `States`.
  - **Class subjects**: `can('read', Post)` is `Can<Post>("read")`, and checks pass `typeof(Post)`.
  - **"has rules with `ast` property"**: the parsed condition is internal, so the test checks the rule's public
    `Conditions`.
- `builder.spec.ts`: multiple subjects are a list (`Can(["read"], ["Post", "User"])`); a C# parameter can't be
  "a string or an array".
- `error.spec.ts`
  - The reason test builds a new ability instead of calling `update`.
  - `setDefaultMessage` is the `ForbiddenMessage` option.
- `rulesToAST.spec.ts`
  - **The 4 AST shape tests** check the same structure through `ToMongoQuery`, which uses `$nor` where CASL's
    AST uses `not`.
  - **The 17 "interprets AST the same way as Ability" tests** check that `Filter<Post>(action)`, compiled and run
    in memory, agrees with `Can` for every object. Each runs twice, under CASL semantics and under
    deny-overrides. `Filter` is the expression EF Core translates to SQL, so this is the .NET counterpart of
    CASL's AST.

## Differences from CASL that the tests don't cover

- **Strict registries.** With `new SubjectRegistry().Add<T>(…)`, conditions may only use registered member
  paths, field-level rules may only name registered fields, and rules on `all` can't have conditions or fields.
  CASL has no such mode; `SubjectRegistry.Open()`, the default for `new AbilityBuilder()`, behaves like CASL.
- **Deny-overrides.** `AbilityOptions.Semantics = RuleSemantics.DenyOverrides` makes any matching `cannot` rule
  win, whatever the order. `ToCaslRules()` then puts `cannot` rules last, so `@casl/ability` reaches the same
  answers.
- **Empty conditions.** `{}` counts as "no conditions". CASL keeps it as a condition that matches everything.
  Every check gives the same answer; only `RulesToCondition` output differs (`{}` rather than a branch per rule).
- **Packed rules.** `PackedRules.Unpack` refuses an element of the wrong type, where `unpackRules` keeps it: for
  example `1` as conditions, which three of CASL's pack tests use while testing the fields and reason elements. The
  ported tests use `0` (no conditions) there.
- **Validation.** Unknown operators, values of the wrong type and invalid regular expressions fail when the
  ability is built, not when it is first used.
