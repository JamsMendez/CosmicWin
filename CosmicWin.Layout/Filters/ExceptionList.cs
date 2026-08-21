namespace CosmicWin.Layout.Filters;

/// <summary>
/// An immutable, user-editable manual exception list (spec WE-2). Applied in addition to WE-1's
/// automatic heuristics via <see cref="WindowFilters.IsExcluded"/>.
/// </summary>
public sealed class ExceptionList
{
    private readonly IReadOnlyList<ExceptionRule> _rules;

    public ExceptionList(IReadOnlyList<ExceptionRule> rules) => _rules = rules;

    /// <summary>An exception list with no entries — matches nothing.</summary>
    public static ExceptionList Empty { get; } = new([]);

    /// <summary><see langword="true"/> if any rule matches <paramref name="descriptor"/>.</summary>
    public bool Matches(WindowDescriptor descriptor)
    {
        foreach (var rule in _rules)
        {
            var matched = rule.Kind switch
            {
                ExceptionRuleKind.ProcessName => string.Equals(descriptor.ProcessName, rule.Value, StringComparison.OrdinalIgnoreCase),
                ExceptionRuleKind.TitleSubstring => descriptor.Title.Contains(rule.Value, StringComparison.OrdinalIgnoreCase),
                ExceptionRuleKind.ClassName => string.Equals(descriptor.ClassName, rule.Value, StringComparison.OrdinalIgnoreCase),
                _ => false,
            };

            if (matched)
            {
                return true;
            }
        }

        return false;
    }
}
