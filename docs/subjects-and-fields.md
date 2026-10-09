# Subjects and fields

A subject type is the kind of thing a rule is about: `"Post"`, `"WorkItem"`. A `SubjectRegistry` maps subject types to
CLR types and says what rules may use from them.

## Open and strict registries

| | Open: `SubjectRegistry.Open()` | Strict: `new SubjectRegistry()` |
|---|---|---|
| Used by | `new AbilityBuilder()` when no registry is given | `new AbilityBuilder(subjects)` |
| Subject names | Any name | Only the names you `Add` (and `all`) |
| An object's subject type | Its registered name, else its class name | Its registered name; an unregistered type throws |
| Condition paths | Any public member | Only the paths you list with `ConditionFields` |
| Field-level rules | Any field | Only the fields you list with `Fields` |
| Use it when | Developers write the rules | Rules come from users or a database |

```csharp
var subjects = new SubjectRegistry()
    .Add<WorkItem>("WorkItem", s => s
        .ConditionFields("projectId", "assigneeId", "status", "labels", "project.orgUnitId", "comments.authorId")
        .ConditionField(x => x.Priority)                       // the same, from an expression: "priority"
        .Fields("summary", "status", "assigneeId", "priority"))
    .Add<Project>("Project", s => s.ConditionFields("id", "orgUnitId"));
```

Condition paths are checked when the registry is built, so a typo fails at startup rather than on a request. With a
strict registry, a rule on `all` can't have conditions or fields, because there's no type to check them against: name
the subjects instead.

An open registry may still list subjects. A registered subject keeps its name (useful when the class name isn't the
name rules use), and listing condition paths makes it strict for that subject.

## Field-level rules

A rule can be limited to fields. Field names are the ones your application uses; they don't have to be members of the
type.

```csharp
var ability = new AbilityBuilder(subjects)
    .Can("read", "WorkItem")
    .Can("update", "WorkItem", new { assigneeId = userId }, "summary", "status")
    .Cannot("update", "WorkItem", new { status = "Done" }, reason: "Done items are frozen", "status")
    .Build();

ability.Can("update", item, "summary");        // one field
ability.Can("update", item);                   // the item as a whole
ability.PermittedFields("update", item);       // ["summary"] for a done item, ["summary", "status"] otherwise
```

These follow CASL:
- **A check without a field** counts allow rules limited to fields (you may update *something*) and ignores `cannot`
  rules limited to fields.
- **`AccessibleBy` ignores `cannot` rules limited to fields.** Hiding a field doesn't hide the row.
- **`PermittedFields`** lists the fields allowed for an object or a subject type. A rule without fields covers every
  field the strict registry lists for the subject; pass `fieldsFrom` to say otherwise, or register the fields.

### Patterns

Rule fields may use CASL's patterns:

| Pattern | Matches |
|---|---|
| `author.*` | `author` and `author.name`, not `author.address.city` |
| `author.**` | `author` and everything under it, at any depth |
| `author.*.name` | `author.home.name` |
| `*.name` | `author.name`, `editor.name` |
| `**.name` | `author.name`, `post.author.name` |
| `street*` | `street`, `streetName` |

### Field families

Fields declared at run time (custom fields, say) can't all be listed at startup. A registry field ending in `.**`
accepts every field under it:

```csharp
var subjects = new SubjectRegistry()
    .Add<WorkItem>("WorkItem", s => s.ConditionFields("status").Fields("summary", "fields.**"));

new AbilityBuilder(subjects).Can("read", "WorkItem", fields: "fields.custom.cost").Build();   // accepted
new AbilityBuilder(subjects).Can("read", "WorkItem", fields: "cost").Build();                // refused
```

### Fields that must be granted by name

Some fields should only be visible when a rule names them, such as a salary or a cost. CASL's own semantics let a
rule without fields cover every field, so a plain "read work items" grant would reveal them. `FieldMatch.Named` changes
that for one check: only allow rules that list the field (or a pattern matching it) grant it, while `cannot` rules
count as usual.

```csharp
var member = new AbilityBuilder(subjects).Can("read", "WorkItem").Build();
var finance = new AbilityBuilder(subjects)
    .Can("read", "WorkItem")
    .Can("read", "WorkItem", fields: "cost.*")
    .Build();

member.Can("read", item, "cost.total");                       // true: a rule without fields covers every field
member.Can("read", item, "cost.total", FieldMatch.Named);     // false
finance.Can("read", item, "cost.total", FieldMatch.Named);    // true
finance.Can("read", item, "summary", FieldMatch.Named);       // false: no rule names it
```

Use `FieldMatch.Named` for the sensitive fields and the ordinary check for the rest.

## Tagged objects and custom detection

When an object's class doesn't say what it is (a DTO, a dictionary), tag it: `Subject.Of("Article", dto)`. To decide
subject types yourself, set `AbilityOptions.DetectSubjectType`, a function from an object to its subject type name
(return null to fall back to the default).
