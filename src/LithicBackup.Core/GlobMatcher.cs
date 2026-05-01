using System.Text.RegularExpressions;
using LithicBackup.Core.Models;

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
    /// Create a combined exclusion predicate from global patterns (backward compat)
    /// and per-directory patterns in the source tree. A file is excluded if either
    /// the global filter or the tree filter matches.
    /// Returns null if no patterns exist in either source.
    /// </summary>
    public static Func<string, bool>? CreateCombinedFilter(
        IReadOnlyList<string> globalPatterns,
        IReadOnlyList<SourceSelection> sources)
    {
        var globalFilter = CreateFilter(globalPatterns);
        var treeFilter = CreateTreeFilter(sources);
        return (globalFilter, treeFilter) switch
        {
            (not null, not null) => path => globalFilter(path) || treeFilter(path),
            (not null, null) => globalFilter,
            (null, not null) => treeFilter,
            _ => null,
        };
    }

    /// <summary>
    /// Create a predicate that tests whether a file path is excluded by the
    /// per-directory exclusion/inclusion patterns in a <see cref="SourceSelection"/>
    /// tree. Patterns on parent nodes are inherited by children; inclusion
    /// patterns override inherited exclusions.
    /// Returns null if no patterns exist anywhere in the tree.
    /// </summary>
    public static Func<string, bool>? CreateTreeFilter(IReadOnlyList<SourceSelection> roots)
    {
        // Build a dictionary: normalised directory path → (exclude filter, include filter).
        // Only entries that have patterns are stored.
        var dirFilters = new Dictionary<string, (Func<string, bool>? Exclude, Func<string, bool>? Include)>(
            StringComparer.OrdinalIgnoreCase);

        bool anyPatterns = false;
        CollectTreePatterns(roots, dirFilters, ref anyPatterns);

        if (!anyPatterns)
            return null;

        return fullPath =>
        {
            bool excluded = false;

            // Check the virtual root node first (path = "", shallowest level).
            if (dirFilters.TryGetValue("", out var rootFilters))
            {
                if (rootFilters.Exclude is not null && rootFilters.Exclude(fullPath))
                    excluded = true;
                if (excluded && rootFilters.Include is not null && rootFilters.Include(fullPath))
                    excluded = false;
            }

            // Walk from the file's directory up to the drive root, collecting
            // all applicable exclusion and inclusion verdicts.
            string? dir = Path.GetDirectoryName(fullPath);

            // Collect the chain of ancestor directories from deepest to shallowest.
            var chain = new List<string>();
            string? current = dir;
            while (current is not null)
            {
                chain.Add(current);
                string? parent = Path.GetDirectoryName(current);
                if (parent == current) break; // root
                current = parent;
            }

            // Walk shallowest-first so deeper inclusions can override shallower exclusions.
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                if (!dirFilters.TryGetValue(chain[i], out var filters))
                    continue;

                if (filters.Exclude is not null && filters.Exclude(fullPath))
                    excluded = true;

                if (excluded && filters.Include is not null && filters.Include(fullPath))
                    excluded = false;
            }

            return excluded;
        };
    }

    private static void CollectTreePatterns(
        IEnumerable<SourceSelection> nodes,
        Dictionary<string, (Func<string, bool>? Exclude, Func<string, bool>? Include)> result,
        ref bool anyPatterns)
    {
        foreach (var node in nodes)
        {
            if (!node.IsDirectory) continue;

            Func<string, bool>? excludeFilter = null;
            Func<string, bool>? includeFilter = null;

            if (node.ExcludedPatterns.Count > 0)
            {
                excludeFilter = CreateFilter(node.ExcludedPatterns);
                if (excludeFilter is not null) anyPatterns = true;
            }
            if (node.IncludedPatterns.Count > 0)
            {
                includeFilter = CreateFilter(node.IncludedPatterns);
                if (includeFilter is not null) anyPatterns = true;
            }

            if (excludeFilter is not null || includeFilter is not null)
                result[node.Path] = (excludeFilter, includeFilter);

            CollectTreePatterns(node.Children, result, ref anyPatterns);
        }
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
