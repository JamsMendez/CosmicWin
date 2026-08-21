namespace CosmicWin.Layout.Filters;

/// <summary>
/// Parses the plain-text manual exception-list config file format (spec WE-2) into an
/// <see cref="ExceptionList"/>. Takes raw text content rather than a file path so
/// <c>CosmicWin.Layout</c> stays Win32/IO-free (design D1/D8); the App layer owns reading the
/// file from disk and re-invoking <see cref="Parse"/> on change (WE-3).
///
/// Format: one rule per line, <c>kind:value</c> where <c>kind</c> is <c>process</c>, <c>title</c>,
/// or <c>class</c> (case-insensitive). Blank lines and lines starting with <c>#</c> are ignored.
/// Unrecognized kinds and malformed lines are skipped rather than throwing, so a manually-edited
/// config file with a typo degrades gracefully instead of blocking startup/reload.
/// </summary>
public static class ExceptionListLoader
{
    public static ExceptionList Parse(string content)
    {
        var rules = new List<ExceptionRule>();

        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0 || separatorIndex == line.Length - 1)
            {
                continue;
            }

            var kindToken = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();

            ExceptionRuleKind? kind = kindToken.ToLowerInvariant() switch
            {
                "process" => ExceptionRuleKind.ProcessName,
                "title" => ExceptionRuleKind.TitleSubstring,
                "class" => ExceptionRuleKind.ClassName,
                _ => null,
            };

            if (kind is null || value.Length == 0)
            {
                continue;
            }

            rules.Add(new ExceptionRule(kind.Value, value));
        }

        return new ExceptionList(rules);
    }
}
