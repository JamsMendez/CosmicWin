namespace CosmicWin.Layout.Filters;

/// <summary>The kind of value a manual <see cref="ExceptionRule"/> (WE-2) matches against.</summary>
public enum ExceptionRuleKind
{
    /// <summary>Exact, case-insensitive match against <see cref="WindowDescriptor.ProcessName"/>.</summary>
    ProcessName,

    /// <summary>Case-insensitive substring match against <see cref="WindowDescriptor.Title"/>.</summary>
    TitleSubstring,

    /// <summary>Exact, case-insensitive match against <see cref="WindowDescriptor.ClassName"/>.</summary>
    ClassName,
}

/// <summary>One manual exception-list entry.</summary>
public readonly record struct ExceptionRule(ExceptionRuleKind Kind, string Value);
