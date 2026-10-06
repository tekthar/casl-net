import { buildMongoQueryMatcher, createMongoAbility } from '@casl/ability';
import { $and, $or, $nor, $not, and, or, nor, not } from '@ucast/mongo2js';

/** CASL's MongoDB matcher with the logical operators CASL .NET rules may contain. */
export const conditionsMatcher = buildMongoQueryMatcher({ $and, $or, $nor, $not }, { and, or, nor, not });

/**
 * Creates a CASL ability from the rules CASL .NET exports (Ability.ToCaslRules()).
 * @param {import('@casl/ability').RawRuleOf<import('@casl/ability').MongoAbility>[]} rules
 * @param {import('@casl/ability').AbilityOptionsOf<import('@casl/ability').MongoAbility>} [options]
 */
export function createAbility(rules, options = {}) {
  return createMongoAbility(rules, { conditionsMatcher: conditionsMatcher, ...options });
}
