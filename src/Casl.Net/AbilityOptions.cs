namespace Casl;

/// <summary>How rules combine when more than one matches.</summary>
public enum RuleSemantics
{
    /// <summary>
    /// CASL's semantics, the default: rules are checked from the last defined to the first, and the first one that
    /// matches decides. A later <c>can</c> can undo an earlier <c>cannot</c> and vice versa.
    /// </summary>
    Casl,

    /// <summary>
    /// Access is granted when at least one allow rule matches and no <c>cannot</c> rule matches, whatever their order.
    /// Safer when rules are assembled from several sources (for example role grants) whose order means nothing.
    /// </summary>
    DenyOverrides,
}

public sealed class AbilityOptions
{
    public RuleSemantics Semantics { get; init; } = RuleSemantics.Casl;

    /// <summary>Action aliases, e.g. <c>modify</c> = <c>update</c> + <c>delete</c>. See <see cref="ActionAliases"/>.</summary>
    public ActionAliases? Aliases { get; init; }

    /// <summary>The action that means "every action". CASL's default is <c>manage</c>.</summary>
    public string AnyAction { get; init; } = SubjectRegistry.AllActions;

    /// <summary>The subject type that means "every subject", also used for rules without a subject. CASL's default is <c>all</c>.</summary>
    public string AnySubjectType { get; init; } = SubjectRegistry.AllSubjects;

    /// <summary>Overrides how an object's subject type is found. Return null to fall back to the default.</summary>
    public Func<object, string?>? DetectSubjectType { get; init; }

    /// <summary>Message for <see cref="ForbiddenException"/> when no rule gives a reason. CASL's default: Cannot execute "action" on "Subject".</summary>
    public Func<ForbiddenException, string>? ForbiddenMessage { get; init; }

    internal static AbilityOptions Default { get; } = new();
}
