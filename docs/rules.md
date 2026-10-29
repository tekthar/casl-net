# Rules

A rule says that an action is allowed (or, inverted, forbidden) on a subject type, optionally only for instances that
match conditions and only for some fields. An `Ability` is a list of rules with their placeholders filled in.

## The format

Rules use CASL's raw format, so the same JSON works in .NET and in `@casl/ability`:

```json
{
  "action": ["update", "assign"],
  "subject": "WorkItem",
  "conditions": { "assigneeId": "${user.id}", "status": { "$ne": "Done" } },
  "fields": ["summary", "status"],
  "inverted": false,
  "reason": "Only the assignee can change an open item"
}
```

| Property | Meaning |
|---|---|
| `action` | One action or a list. `manage` means every action. |
| `subject` | One subject type or a list. `all` means every subject. No subject makes a claim rule (see below). |
| `conditions` | A MongoDB-style query the instance must match ([Conditions](conditions.md)). None (or `{}`) means every instance. |
| `fields` | The fields the rule is limited to, with patterns allowed ([Subjects and fields](subjects-and-fields.md)). None means all fields. |
| `inverted` | `true` for a `cannot` rule. |
| `reason` | The message when this rule denies access. |

In C#, `RawRule` is the same thing: `RawRule.Parse(json)`, `RawRule.ParseMany(json)` for an array, `rule.ToJson()`,
or `RawRule.Can(...)` and `RawRule.Cannot(...)`.

## Writing rules in code

```csharp
var ability = new AbilityBuilder()
    .Can("read", "Article")                                        // every article
    .Can("update", "Article", new { authorId = userId })           // their own
    .Can("update", "Article", fields: ["title", "body"])           // only some fields
    .Cannot("delete", "Article", new { published = true }, reason: "Published articles stay")
    .Can(["publish", "archive"], ["Article", "Page"])               // several actions and subjects
    .Can<Comment>("create")                                        // by CLR type
    .Can("login")                                                  // a claim rule: no subject
    .Build();
```

Each `Can` and `Cannot` returns a `RuleBuilder` that refines the rule just added: `.Where(conditions)`,
`.OnFields(...)` and `.Because(reason)`. Conditions can be an anonymous object, a `JsonObject` or JSON text.

## Checking

```csharp
ability.Can("update", article);                    // an object: its conditions are checked
ability.Can("update", article, "title");           // one field
ability.Can("update", "Article");                  // a type: "on at least some articles?"
ability.Can("update", typeof(Article));            // the same, by CLR type
ability.Can("read", Subject.Of("Article", dto));   // an object whose class doesn't say what it is
ability.Can("login");                              // a claim rule
ability.Cannot("delete", article);                 // the opposite of Can

ability.EnsureCan("update", article);              // throws ForbiddenException
ForbiddenException? why = ability.UnlessCan("update", article);   // null when allowed
```

A type-level check (`"Article"`, `typeof(Article)`) answers whether the action is allowed on some instances, which is
what menus and buttons need. Allow rules with conditions count; `cannot` rules with conditions don't.

`ForbiddenException` carries `Action`, `Subject`, `SubjectType`, `Field` and `Reason`. Its message is, in order: the
message passed to `EnsureCan`, the deciding rule's `reason`, `AbilityOptions.ForbiddenMessage`, then
`Cannot execute "update" on "Article"`.

## How rules combine

**CASL's semantics, the default: the last matching rule wins.** Rules are checked from the last added to the first,
and the first that matches decides:

```csharp
new AbilityBuilder()
    .Can("read", "Post")
    .Cannot("read", "Post", new { secret = true })   // secret posts hidden
    .Build();

new AbilityBuilder()
    .Cannot("read", "Post", new { secret = true })
    .Can("read", "Post")                             // added later: secret posts readable again
    .Build();
```

**Deny-overrides:** with `new AbilityOptions { Semantics = RuleSemantics.DenyOverrides }`, access is granted when an
allow rule matches and no `cannot` rule matches, whatever the order. Use it when rules are assembled from several roles
or grants, where order means nothing and an earlier grant must not be undone by a later one.

```csharp
var ability = new AbilityBuilder(subjects, new AbilityOptions { Semantics = RuleSemantics.DenyOverrides })
    .AddRange(rulesFromAllGrants)
    .Build();
```

Both semantics give the same answers in memory, in SQL and in the browser: with deny-overrides, `ToCaslRules()` puts
`cannot` rules last so CASL's "last rule wins" agrees.

## Actions and subjects

- `manage` and `all` mean every action and every subject. Rename them with `AbilityOptions.AnyAction` and
  `AnySubjectType`.
- **Aliases** make one action stand for others:

  ```csharp
  var options = new AbilityOptions
  {
      Aliases = ActionAliases.Create(new Dictionary<string, string[]> { ["modify"] = ["update", "delete"] }),
  };
  ```

  A rule for `modify` then covers `update` and `delete`. Aliases can nest; cycles and aliases of `manage` are refused.
- **Subject type detection:** by default an object's subject type is the registered name for its CLR type (its base
  types count, so EF Core proxies work), or its class name in an open registry. `AbilityOptions.DetectSubjectType`
  overrides that; `Subject.Of("Article", value)` tags one object.

## Placeholders and variables

Conditions may contain placeholders, filled in when the ability is built:

```csharp
var ability = new AbilityBuilder(subjects)
    .WithVariables(new { user = new { id = userId, teamIds } })
    .Add(RawRule.Parse("""{ "action": "read", "subject": "WorkItem", "conditions": { "teamId": { "$in": "${user.teamIds}" } } }"""))
    .AddRange(grantRules, variables: new { grant = new { projectIds = new[] { 7, 9 } } })
    .Build();
```

- A string that is exactly a placeholder (`"${user.teamIds}"`) becomes the variable's value, of any JSON type: an
  array, a number, an object. A placeholder inside a longer string is written as text (`"team-${user.id}"`).
- Variables are anonymous objects, dictionaries or `JsonObject`s, looked up by dotted path, ignoring case.
- Variables given to `Add` or `AddRange` apply to those rules only and win over `WithVariables`.
- **A placeholder with no value fails the build**, whether the variable is missing or null. Dropping it silently could
  grant more or less than intended: `{ "assigneeId": "${user.id}" }` with a null id would match every unassigned item.

## Rules fail closed

`Build()` checks every rule and throws `CaslConfigurationException` for anything it can't enforce exactly: an unknown
subject, member path, operator, field or placeholder, or a value of the wrong type. Nothing is skipped, because
skipping a `cannot` rule would widen access.

To check a rule before saving it (an admin screen, an import), use `AbilityBuilder.Validate(rule, subjects)`. It
returns the problems found, as messages, and accepts placeholders without their values.

## Looking inside an ability

| | |
|---|---|
| `Rules` | The rules with placeholders filled in, in the order they were added |
| `RulesFor(action, subjectType, field)` | The rules that apply, highest priority first |
| `PossibleRulesFor(action, subjectType)` | The same, ignoring fields |
| `RelevantRuleFor(action, subject, field)` | The rule that decides a check, or null when none matches |
| `ActionsFor(subjectType)` | Every action rules mention for a subject type |
| `PermittedFields(action, subject)` | The fields a person may act on ([Subjects and fields](subjects-and-fields.md)) |
| `DefaultValues(action, subjectType)` | Plain values from allow rules' conditions, as defaults for a new object |

Abilities are immutable: build a new one when rules change. One ability can be shared across threads.
