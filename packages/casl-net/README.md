# casl-net

[![npm](https://img.shields.io/npm/v/casl-net.svg)](https://www.npmjs.com/package/casl-net)

Use [CASL .NET](https://github.com/tekthar/casl-net) rules in the browser.

CASL .NET is a .NET implementation of [CASL](https://casl.js.org)'s rule format and semantics. The server builds an
ability, sends `ability.ToCaslRules()`, and the browser checks the same rules with the real
[`@casl/ability`](https://www.npmjs.com/package/@casl/ability), so the interface hides what the server would refuse.

This package configures CASL with the MongoDB logical operators (`$and`, `$or`, `$nor`, `$not`) that CASL .NET rules
may contain, so both sides reach the same decisions. The repository's compatibility test checks that on every build:
24,000 decisions on 150 random rule sets, all identical.

## Install

```bash
npm install casl-net @casl/ability
```

`@casl/ability` 6.7 or 7 is a peer dependency.

## Use

```js
import { createAbility } from 'casl-net';
import { subject } from '@casl/ability';

const rules = await fetch('/api/me/rules').then(r => r.json());   // ability.ToCaslRules() on the server
const ability = createAbility(rules);

ability.can('update', subject('WorkItem', item));
ability.can('update', subject('WorkItem', item), 'status');
ability.can('create', 'WorkItem');
```

## API

- **`createAbility(rules, options?)`**: a `MongoAbility` from `@casl/ability`'s `createMongoAbility`, with the
  conditions matcher below. `options` are CASL's own and override the defaults.
- **`conditionsMatcher`**: CASL's MongoDB matcher with `$and`, `$or`, `$nor` and `$not` switched on, for building an
  ability yourself.

Types are included.

## More

The [browser guide](https://github.com/tekthar/casl-net/blob/main/docs/browser.md) covers packed rules, field checks
and what the server sends. The .NET package is [`Casl.Net`](https://www.nuget.org/packages/Casl.Net).

Apache-2.0. Not affiliated with or endorsed by CASL or its author.
