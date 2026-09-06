using System.Text.RegularExpressions;

namespace LithicBackup.Core;

/// <summary>
/// Converts glob/wildcard patterns into compiled predicates for file exclusion.
///
/// <b>Filename patterns</b> (no path separator):
///   *.log        — all files ending in .log
///   temp_*       — all files starting with temp_
///   debug*.txt   — files like debug.txt, debug1.txt, debug_old.txt
///   *.min.js     — files ending in .min.js
///   .log         — legacy extension form, treated as *.log
///   log          — bare word, treated as *.log
///
/// <b>Path patterns</b> (contain / or \):
///   */.vs/*      — any file inside a .vs directory
///   */bin/*      — any file inside a bin directory
///   */obj/*.json — .json files inside any obj directory
///
/// Filename patterns match against the file name only.
/// Path patterns match against the full normalised path (separators → /).
/// Matching is always case-insensitive.
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

        var fileNameRegexes = new List<Regex>();
        var pathRegexes = new List<Regex>();

        foreach (var pattern in patterns)
        {
            string trimmed = pattern.Trim();
            if (trimmed.Length == 0)
                continue;

            // Strip the no-version marker — it's metadata for the backup
            // engine, not part of the glob pattern.
            if (trimmed.StartsWith("~nv:"))
                trimmed = trimmed[4..];

            if (IsPathPattern(trimmed))
                pathRegexes.Add(GlobToRegex(trimmed, isPathPattern: true));
            else
                fileNameRegexes.Add(GlobToRegex(trimmed, isPathPattern: false));
        }

        if (fileNameRegexes.Count == 0 && pathRegexes.Count == 0)
            return null;

        var fnArr = fileNameRegexes.ToArray();
        var pathArr = pathRegexes.ToArray();

        return fullPath =>
        {
            // Filename-only patterns: match against the file name.
            if (fnArr.Length > 0)
            {
                string fileName = Path.GetFileName(fullPath);
                if (!string.IsNullOrEmpty(fileName))
                {
                    for (int i = 0; i < fnArr.Length; i++)
                    {
                        if (fnArr[i].IsMatch(fileName))
                            return true;
                    }
                }
            }

            // Path patterns: match against the full path with normalised separators.
            if (pathArr.Length > 0)
            {
                string normalised = fullPath.Replace('\\', '/');
                for (int i = 0; i < pathArr.Length; i++)
                {
                    if (pathArr[i].IsMatch(normalised))
                        return true;
                }
            }

            return false;
        };
    }

    /// <summary>
    /// Build a predicate that answers a STRICTER question than
    /// <see cref="CreateFilter"/>: is <em>every file anywhere beneath this
    /// directory</em> excluded? Returns null when no pattern can promise that.
    ///
    /// <para>It exists so a scan can skip an excluded subtree instead of walking
    /// it and rejecting the files one at a time. On a real set that walk was the
    /// bulk of the scan: hundreds of thousands of directories under build/,
    /// debug/ and target/ trees, on a spinning disk, none of which could
    /// contribute a single file.</para>
    ///
    /// <para><b>It has to be conservative, because being wrong means files are
    /// silently not backed up.</b> Only patterns that end in <c>/*</c> or
    /// <c>/**</c> qualify: the part before that suffix becomes an anchored regex
    /// matched against the directory itself, so <c>**/debug/**</c> prunes a
    /// directory named <c>debug</c> while <c>**/debug/*.obj</c> prunes nothing —
    /// it only excludes SOME of the files there. Filename-only patterns
    /// (<c>*.raw</c>) can never prune, because a directory may hold files of any
    /// name.</para>
    ///
    /// <para><b>It answers only about the directory it is given.</b> With
    /// <c>**/build/**</c> it returns true for <c>...uild</c> but FALSE for
    /// <c>...uild\sub</c>, whose files are equally excluded — the prefix
    /// regex is anchored, so the nested path does not match it. That is not a
    /// bug and needs no fix: the scan tests every directory before descending,
    /// so it prunes at <c>build</c> and never asks about anything beneath it.
    /// Only widen this if some caller starts asking bottom-up.</para>
    ///
    /// <para><b>Soundness depends on <c>*</c> crossing separators here</b>
    /// (see <see cref="GlobToRegexPattern"/>: <c>*</c> becomes <c>.*</c>). That
    /// is what makes <c>dir/*</c> cover <c>dir/a/b/c</c> and not just
    /// <c>dir/a</c>. If <c>*</c> is ever changed to stop at a separator, this
    /// method must change with it or it will prune subtrees whose deeper files
    /// are NOT excluded.</para>
    /// </summary>
    public static Func<string, bool>? CreateDirectorySubtreeFilter(IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0)
            return null;

        var prefixRegexes = new List<Regex>();

        foreach (var pattern in patterns)
        {
            string trimmed = pattern.Trim();
            if (trimmed.Length == 0)
                continue;
            if (trimmed.StartsWith("~nv:"))
                trimmed = trimmed[4..];
            if (!IsPathPattern(trimmed))
                continue;   // a filename pattern says nothing about a whole directory

            string normalised = trimmed.Replace('\\', '/');

            string prefix;
            if (normalised.EndsWith("/**", StringComparison.Ordinal))
                prefix = normalised[..^3];
            else if (normalised.EndsWith("/*", StringComparison.Ordinal))
                prefix = normalised[..^2];
            else
                continue;   // does not cover a whole subtree

            prefix = prefix.TrimEnd('/');
            if (prefix.Length == 0)
                continue;   // "/**" would prune everything; refuse

            prefixRegexes.Add(new Regex(
                "^" + GlobToRegexPattern(prefix) + "$",
                RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline));
        }

        if (prefixRegexes.Count == 0)
            return null;

        var arr = prefixRegexes.ToArray();
        return directoryPath =>
        {
            string normalised = directoryPath.Replace('\\', '/').TrimEnd('/');
            for (int i = 0; i < arr.Length; i++)
            {
                if (arr[i].IsMatch(normalised))
                    return true;
            }
            return false;
        };
    }

    /// <summary>
    /// A pattern is path-based if it contains a directory separator.
    /// </summary>
    private static bool IsPathPattern(string pattern)
        => pattern.Contains('/') || pattern.Contains('\\');

    /// <summary>
    /// Convert a single glob pattern to a compiled Regex.
    /// </summary>
    private static Regex GlobToRegex(string pattern, bool isPathPattern)
    {
        string normalised = pattern.Trim();

        if (isPathPattern)
        {
            // Normalise path separators to forward slash so the regex
            // matches the normalised full path.
            normalised = normalised.Replace('\\', '/');
        }
        else
        {
            // Legacy formats: bare word "log" or extension ".log" with no wildcards.
            // Treat as "*.log".
            if (!normalised.Contains('*') && !normalised.Contains('?'))
            {
                if (!normalised.StartsWith('.'))
                    normalised = "." + normalised;

                normalised = "*" + normalised;
            }
        }

        string regexPattern = "^" + GlobToRegexPattern(normalised) + "$";

        return new Regex(regexPattern,
            RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);
    }

    /// <summary>
    /// Translate glob metacharacters to regex equivalents.
    /// * → .*  (match any sequence of chars, including path separators)
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
