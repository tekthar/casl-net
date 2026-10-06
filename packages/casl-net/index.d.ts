import type { AbilityOptionsOf, ConditionsMatcher, MongoAbility, MongoQuery, RawRuleOf } from '@casl/ability';

/** CASL's MongoDB matcher with $and, $or, $nor and $not switched on. */
export declare const conditionsMatcher: ConditionsMatcher<MongoQuery>;

/** Creates a CASL ability from the rules CASL .NET exports (Ability.ToCaslRules()). */
export declare function createAbility(rules: RawRuleOf<MongoAbility>[], options?: AbilityOptionsOf<MongoAbility>): MongoAbility;
