# casl-net

Use [CASL .NET](https://github.com/msar-dev/casl-net) rules in the browser.

The server sends `ability.ToCaslRules()`, and the browser checks them with the real [@casl/ability](https://casl.js.org). This package configures CASL with the MongoDB logical operators (`$and`, `$or`, `$nor`, `$not`) that CASL .NET supports, so both sides reach the same decisions. The repository's compatibility test checks that.

```js
import { createAbility } from 'casl-net';
import { subject } from '@casl/ability';

const ability = createAbility(rulesFromServer);
ability.can('update', subject('WorkItem', item));
```

Apache-2.0.
