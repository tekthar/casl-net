import { buildMongoQueryMatcher, createMongoAbility } from '@casl/ability';
import { $and, $or, $nor, $not, and, or, nor, not } from '@ucast/mongo2js';

/** CASL's MongoDB matcher with the logical operators Msar.Authz rules may contain. */
export const msarConditionsMatcher = buildMongoQueryMatcher({ $and, $or, $nor, $not }, { and, or, nor, not });

/**
 * Creates a CASL ability from the rules Msar.Authz exports (Ability.ToCaslRules()).
 * @param {import('@casl/ability').RawRuleOf<import('@casl/ability').MongoAbility>[]} rules
 * @param {import('@casl/ability').AbilityOptionsOf<import('@casl/ability').MongoAbility>} [options]
 */
export function createMsarAbility(rules, options = {}) {
  return createMongoAbility(rules, { conditionsMatcher: msarConditionsMatcher, ...options });
}
