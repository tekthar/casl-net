# @msar/authz-casl

Use [Msar.Authz](https://github.com/msar-dev/msar-authz) rules in the browser.

The server sends `ability.ToCaslRules()`, and the browser checks them with the real [@casl/ability](https://casl.js.org). This package configures CASL with the MongoDB logical operators (`$and`, `$or`, `$nor`, `$not`) that Msar.Authz supports, so both sides reach the same decisions. The repository's compatibility test checks that.

```js
import { createMsarAbility } from '@msar/authz-casl';
import { subject } from '@casl/ability';

const ability = createMsarAbility(rulesFromServer);
ability.can('update', subject('WorkItem', item));
```

Apache-2.0.
