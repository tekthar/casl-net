import type { AbilityOptionsOf, ConditionsMatcher, MongoAbility, MongoQuery, RawRuleOf } from '@casl/ability';

/** CASL's MongoDB matcher with $and, $or, $nor and $not switched on. */
export declare const msarConditionsMatcher: ConditionsMatcher<MongoQuery>;

/** Creates a CASL ability from the rules Msar.Authz exports (Ability.ToCaslRules()). */
export declare function createMsarAbility(rules: RawRuleOf<MongoAbility>[], options?: AbilityOptionsOf<MongoAbility>): MongoAbility;
