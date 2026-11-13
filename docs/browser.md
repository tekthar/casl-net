# The browser

The rules that decide on the server can decide in the browser too, so the UI hides what the server would refuse. The
server sends `ability.ToCaslRules()`; the browser checks them with the real [`@casl/ability`](https://casl.js.org),
configured by the `casl-net` npm package.

## On the server

```csharp
app.MapGet("/api/me/rules", async (IAbilityProvider abilities, ClaimsPrincipal user) =>
{
    Guid userId = Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
    return (await abilities.ForUserAsync(userId)).ToCaslRules();
}).RequireAuthorization();
```

`ToCaslRules()` returns the rules in CASL's raw format with placeholders filled in, so the browser never sees
`${user.id}`. It also:
- **expands action aliases**, so the browser doesn't need the alias map;
- **orders rules for deny-overrides**: allow rules first and `cannot` rules last, so CASL's "last rule wins" reaches
  the same answers;
- **adjusts two conditions** where CASL's matcher departs from MongoDB, without changing their meaning: ordering
  operators get `$ne: null` alongside (CASL treats a missing value as smaller than any value), and `$in` containing
  `null` moves the null into an `$or` (CASL's `$in: [null]` doesn't match a missing field).

The rules show what a person may do, including condition values such as project ids. Send them only to that person.

## In the browser

```bash
npm install casl-net @casl/ability
```

```js
import { createAbility } from 'casl-net';
import { subject } from '@casl/ability';

const rules = await fetch('/api/me/rules').then(r => r.json());
const ability = createAbility(rules);

ability.can('update', subject('WorkItem', item));            // the same answer as the server
ability.can('update', subject('WorkItem', item), 'status');  // one field
ability.can('create', 'WorkItem');                            // "on some work items?", for buttons and menus
```

`createAbility(rules, options)` is `createMongoAbility` from `@casl/ability` with the MongoDB logical operators
(`$and`, `$or`, `$nor`, `$not`) switched on, which CASL .NET's rules may contain and CASL's default matcher leaves out.
Pass any other CASL option in `options`. The package also exports the `conditionsMatcher` it uses, for building the
ability yourself. `@casl/ability` 6.7 and 7 work.

Plain objects need their subject type, as in any CASL app: `subject('WorkItem', item)`, or CASL's `detectSubjectType`
option.

**Use `@casl/ability` 7.1.3 or later if the objects you check come from outside your code.** CASL names a plain
object's type from its `__caslSubjectType__` property or, without one, from `constructor.modelName` or
`constructor.name`. Before 7.1.1 it trusted the first even when `subject()` hadn't set it, and before 7.1.3 it read an
object's own `constructor` property too. JSON from an API response, a URL or user input could therefore name its own
type: `JSON.parse('{"constructor":{"name":"Invoice"}}')` was checked as an `Invoice`. From 7.1.3 CASL ignores both: it
accepts `__caslSubjectType__` only when `subject()` set it (otherwise it throws "Untrusted subject type"), and it treats
an own `constructor` property as a plain `Object`. `casl-net` still accepts 6.7 and 7.0 so existing apps keep working,
but those versions don't have these checks. If you stay on them, never check a parsed object without passing it
through `subject()` first, which sets the type on every version.

From 7.1.1, `subject()` marks objects for the copy of `@casl/ability` that ran it. Keep a single copy in your app
(`npm ls @casl/ability`): an object tagged by one copy and checked by another throws "Untrusted subject type".

## Packed rules

For many rules, CASL's compact format is smaller on the wire:

```csharp
JsonArray packed = PackedRules.Pack(ability.ToCaslRules());
```

```js
import { unpackRules } from '@casl/ability/extra';
const ability = createAbility(unpackRules(packed));
```

`PackedRules.Unpack` reads packed rules in .NET too, including ones packed by CASL.

## The same answers

The repository's compatibility test generates 150 random rule sets, exports them with `ToCaslRules()`, and checks that
`@casl/ability` 7 (through `casl-net`) makes the same 24,000 decisions as the .NET library. It runs on every build.

The browser is for the interface only: always check again on the server, which is where `EnsureCan` and
`AccessibleBy` enforce the rules.
