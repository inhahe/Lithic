using System.Text.RegularExpressions;

namespace LithicBackup.Core;

/// <summary>
/// Converts glob/wildcard patterns (e.g. "*.log", "temp_*", "debug*.txt")
/// into a compiled predicate that matches against file names.
///
/// Supported patterns:
///   *.log        — all files ending in .log
///   temp_*       — all files starting with temp_
///   debug*.txt   — files like debug.txt, debug1.txt, debug_old.txt
///   *.min.js     — files ending in .min.js
///   .log         — legacy extension form, treated as *.log
///   log          — bare word, treated as *.log
///
/// Matching is always case-insensitive and against the file name only (not path).
/// </summary>
public static class GlobMatcher
{
    /// <summary>
    /// Create a predicate that tests whether a full file path matches any of
    /// the given exclusion patterns. Returns null if the pattern list is empty.
    /// </summary>
    public static Func<string, bool>? CreateFilter(IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0)
            return null;

        var regexes = new Regex[patterns.Count];
        for (int i = 0; i < patterns.Count; i++)
        {
            regexes[i] = GlobToRegex(patterns[i]);
        }

        return fullPath =>
        {
            string fileName = Path.GetFileName(fullPath);
            if (string.IsNullOrEmpty(fileName))
                return false;

            for (int i = 0; i < regexes.Length; i++)
            {
                if (regexes[i].IsMatch(fileName))
                    return true;
            }

            return false;
        };
    }

    /// <summary>
    /// Convert a single glob pattern to a compiled Regex.
    /// Handles legacy extension-only patterns (e.g. ".log") and bare words ("log")
    /// by treating them as "*.log".
    /// </summary>
    private static Regex GlobToRegex(string pattern)
    {
        string normalized = pattern.Trim();

        // Legacy formats: bare word "log" or extension ".log" with no wildcards.
        // Treat as "*.log" and "*.log" respectively.
        if (!normalized.Contains('*') && !normalized.Contains('?'))
        {
            if (!normalized.StartsWith('.'))
                normalized = "." + normalized;

            // Extension-only: match any filename ending with this extension.
            normalized = "*" + normalized;
        }

        // Convert glob to regex pattern.
        string regexPattern = "^" + GlobToRegexPattern(normalized) + "$";

        return new Regex(regexPattern,
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
    }

    /// <summary>
    /// Translate glob metacharacters to regex equivalents.
    /// * → .*  (match any sequence of chars)
    /// ? → .   (match a single char)
    /// All other characters are escaped for literal matching.
    /// </summary>
    private static string GlobToRegexPattern(string glob)
    {
        var sb = new System.Text.StringBuilder(glob.Length * 2);

        foreach (char c in glob)
        {
            switch (c)
            {
                case '*':
                    sb.Append(".*");
                    break;
                case '?':
                    sb.Append('.');
                    break;
                // Escape regex-special characters for literal matching.
                case '.':
                case '(':
                case ')':
                case '[':
                case ']':
                case '{':
                case '}':
                case '^':
                case '$':
                case '|':
                case '\\':
                case '+':
                    sb.Append('\\').Append(c);
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.ToString();
    }
}
