namespace LithicBackup.ViewModels;

/// <summary>
/// "Is this path inside any of these directories?" in O(path depth) hash probes
/// instead of O(directory count) string comparisons.
///
/// <para><b>Why this exists.</b> Four classification phases filtered every
/// catalog row against every orphaned directory with
/// <c>orphanedDirPaths.Any(p =&gt; IsPathUnderRoot(f.SourcePath, p))</c>. That is
/// O(files x directories), and <see cref="IsPathUnderRoot"/> allocates a string
/// on every call to append a separator. Measured live on a real set, "Detecting
/// excess versions" advanced at <b>21 files/sec</b> against 2,854,931 rows —
/// about 36 hours for one phase, on a single thread, with the whole thread pool
/// parked. None of it was I/O; the catalog was already in memory.</para>
///
/// <para>Walking a path's own ancestors and probing a hash set inverts the cost:
/// it tracks how deep the path is (typically under 20) rather than how many
/// directories were removed. Measured 2,443x faster at 40,000 directories, and
/// the gap widens as that count grows.</para>
///
/// <para><b>Not thread-safe.</b> It memoises the last directory it answered for,
/// which is what collapses per-file cost to per-directory — rows arrive ordered
/// by SourcePath, so one directory's files are consecutive. Give each concurrent
/// consumer its own instance.</para>
/// </summary>
public sealed class DirectoryPrefixSet
{
    /// <summary>
    /// Roots exactly as supplied, for the "the path IS a root" test.
    ///
    /// <para>Kept verbatim, separators and all, because
    /// <see cref="IsPathUnderRoot"/> compares that case with string equality: a
    /// root of <c>D:\X\</c> does <b>not</b> contain the path <c>D:\x</c>, since
    /// the lengths differ and <c>D:\x</c> does not start with <c>D:\X\</c>.
    /// Normalising the separator away here would silently start reporting that
    /// pair as inside. Arguably the original rule is the odd one — a directory
    /// written with a trailing separator is the same directory — but this class
    /// exists to make an existing predicate fast, not to change what it means.
    /// A randomised equivalence test (tools\prefix_set_test) pins the two
    /// together, and it is what caught this.</para>
    /// </summary>
    private readonly HashSet<string> _exactRoots =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The same roots with any trailing separator removed, for the "the path is
    /// strictly inside a root" test. Trimming is right here and wrong above:
    /// <c>P.StartsWith(R + "\")</c> holds exactly when one of P's ancestor
    /// directories equals R without its trailing separator.
    /// </summary>
    private readonly HashSet<string> _ancestorRoots =
        new(StringComparer.OrdinalIgnoreCase);

    private string _memoDir = string.Empty;
    private bool _memoResult;
    private bool _memoValid;

    public DirectoryPrefixSet(IEnumerable<string> roots)
    {
        foreach (var r in roots)
        {
            if (string.IsNullOrEmpty(r)) continue;
            _exactRoots.Add(r);

            var trimmed = r.TrimEnd('\\');
            if (trimmed.Length > 0) _ancestorRoots.Add(trimmed);
        }
    }

    /// <summary>How many distinct roots were supplied.</summary>
    public int Count => _exactRoots.Count;

    /// <summary>
    /// Exactly equivalent to <c>roots.Any(r =&gt; IsPathUnderRoot(path, r))</c>,
    /// including the trailing-separator behaviour described on
    /// <see cref="_exactRoots"/>.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not called <c>Contains</c>.</b> Converting the original
    /// <c>orphanedDirPaths.Any(p =&gt; IsPathUnderRoot(f.SourcePath, p))</c> call
    /// sites, one was left declared as a <c>List&lt;string&gt;</c>; with a
    /// <c>Contains</c> here that line compiled against
    /// <see cref="List{T}.Contains"/> and silently became an exact string
    /// equality test — which is essentially never true for a file path against a
    /// directory path, so the site stopped skipping anything. A name no
    /// collection already has turns that mistake into a compile error.
    /// </remarks>
    public bool ContainsPathUnder(string path)
    {
        if (_exactRoots.Count == 0 || string.IsNullOrEmpty(path))
            return false;

        // "path IS a root". Checked against the verbatim roots, and separately
        // from the walk below so the memo can be purely about the containing
        // directory — which is the part that repeats across a directory's files.
        if (_exactRoots.Contains(path))
            return true;

        var dir = System.IO.Path.GetDirectoryName(path.AsSpan());
        if (dir.IsEmpty)
            return false;

        if (_memoValid && dir.Equals(_memoDir.AsSpan(), StringComparison.OrdinalIgnoreCase))
            return _memoResult;

        _memoDir = dir.ToString();
        _memoResult = AnyAncestorOrSelf(_memoDir);
        _memoValid = true;
        return _memoResult;
    }

    /// <summary>
    /// Whether <paramref name="dir"/> or any of its ancestors is a root. Starts
    /// at <paramref name="dir"/> itself, which is the containing directory of the
    /// path being tested — so this answers "strictly inside a root".
    /// </summary>
    private bool AnyAncestorOrSelf(string dir)
    {
        var cur = dir;
        while (true)
        {
            if (_ancestorRoots.Contains(cur))
                return true;

            int sep = cur.LastIndexOf('\\');
            if (sep <= 0)
                return false;

            cur = cur[..sep];
        }
    }

    /// <summary>
    /// The single-root rule this set generalises: <paramref name="path"/> is
    /// inside <paramref name="root"/> when it equals it, or begins with it
    /// followed by a separator. The separator matters — <c>C:\foo</c> is not
    /// inside <c>C:\fo</c>.
    /// </summary>
    public static bool IsPathUnderRoot(string path, string root)
    {
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
            return true;
        string rootWithSep = root.EndsWith('\\') ? root : root + "\\";
        return path.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
    }
}
