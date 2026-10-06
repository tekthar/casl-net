// Run `dotnet test` first: EquivalenceTests.Write_casl_compatibility_fixture writes fixture.json.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { subject } from '@casl/ability';
import { createAbility } from 'casl-net';

const fixture = JSON.parse(readFileSync(new URL('./fixture.json', import.meta.url), 'utf8'));

test('@casl/ability agrees with CASL .NET on every exported rule set', () => {
  let checks = 0;
  const mismatches = [];
  for (const [caseIndex, c] of fixture.cases.entries()) {
    const ability = createAbility(c.rules);
    for (const check of c.checks) {
      const item = subject('WorkItem', { ...fixture.items[check.item] });
      const actual = check.field === undefined
        ? ability.can(check.action, item)
        : ability.can(check.action, item, check.field);
      checks++;
      if (actual !== check.expected) {
        mismatches.push({ caseIndex, rules: c.rules, item: fixture.items[check.item], check, actual });
      }
    }
  }
  if (mismatches.length > 0) {
    console.log(JSON.stringify(mismatches.slice(0, 3), null, 2));
  }
  assert.equal(mismatches.length, 0, `${mismatches.length} of ${checks} decisions differ`);
  console.log(`${checks} decisions matched across ${fixture.cases.length} rule sets`);
});
