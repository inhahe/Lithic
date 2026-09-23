namespace LithicBackup.Core;

/// <summary>
/// Paths in a volume root that are never user data, and so are never backed up
/// whatever the selection says: NTFS's own metadata, and Windows' own per-volume
/// system items (the recycle bin, System Volume Information, the paging,
/// hibernation and swap files).
///
/// <para><b>NTFS metadata.</b> Two different mechanisms discover files for a
/// backup, and they do not see the same filesystem:</para>
/// <list type="bullet">
///   <item><description>Directory enumeration (the source-selection treeview and
///   the full scan) walks the volume with <c>DirectoryInfo.EnumerateDirectories</c>,
///   which NTFS never reports its metadata entries to. <c>C:\$Extend</c> and its
///   siblings are simply absent — not hidden, not access-denied, <b>absent</b>.
///   </description></item>
///   <item><description>The NTFS USN change journal, which continuous backup reads,
///   reports records for those same entries <b>by name</b>. Resolving such a
///   record yields a perfectly real, openable path like
///   <c>C:\$Extend\$Deleted\001400000008DD22385A431F\mpasbase.vdm</c>.
///   </description></item>
/// </list>
///
/// <para>The consequence was a path that continuous backup happily backed up but
/// that the user could never see, select, or exclude in the GUI, because no
/// enumeration will ever produce it. Worse, the biggest such directory is
/// <c>\$Extend\$Deleted</c> — NTFS's holding area for files deleted with POSIX
/// semantics (or deleted while a handle is still open). A file lands there
/// <i>because it is being deleted</i>, so backing it up captures garbage the user
/// explicitly threw away, under an opaque file-ID name, and re-captures it every
/// time: Windows Defender's 140 MB <c>mpasbase.vdm</c> alone had been stored
/// eight times. Two real backup sets held ~4.2 GB of this. See the
/// "$Extend" entry in known-issues.md.</para>
///
/// <para><b>Windows' system items.</b> These DO show up in a listing, so a set with
/// a whole drive selected used to back them up. <c>$Recycle.Bin</c> is the same
/// garbage as <c>$Extend\$Deleted</c> by another route: what the user deleted.
/// One set was found copying 216,208 files (21 GB) out of <c>D:\$Recycle.Bin</c>,
/// most of them invisible even in the Recycle Bin window because their
/// <c>$I</c> index files were gone. <c>System Volume Information</c> holds restore
/// points, shadow-copy storage and the search index, and is unreadable anyway; the
/// paging, hibernation and swap files are locked by the OS. The editor's tree does
/// not show them either, so nothing it offers to back up is silently skipped. See
/// the recycle-bin entry in known-issues.md.</para>
///
/// <para><b>The invariant.</b> Anything that is never user data must not be backed
/// up, and must not be offered as though it could be. This class is the single
/// definition of that set, applied to the backup hot paths, the scan's directory
/// pruning, the editor's tree, and the worker's set-membership / auto-include-new
/// test, so none of them can drift apart.</para>
/// </summary>
public static class VolumeMetadataPaths
{
    /// <summary>
    /// The entries that sit directly in a volume root and are never user data.
    ///
    /// <para>The NTFS metadata entries all begin with '$' and are never returned by
    /// directory enumeration. <c>$Extend</c> is the one that actually shows up in
    /// USN records in practice (via <c>$Deleted</c>, <c>$RmMetadata</c>,
    /// <c>$Quota</c>, <c>$ObjId</c>, <c>$Reparse</c>). The rest are listed so a
    /// future Windows build that starts journaling them is covered too, rather than
    /// silently reintroducing this bug.</para>
    ///
    /// <para>The Windows system items are enumerable; see the class summary.</para>
    /// </summary>
    private static readonly HashSet<string> RootNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // NTFS metadata.
        "$Extend",
        "$MFT",
        "$MFTMirr",
        "$LogFile",
        "$Volume",
        "$AttrDef",
        "$Bitmap",
        "$Boot",
        "$BadClus",
        "$Secure",
        "$UpCase",

        // Windows' own per-volume items.
        "$Recycle.Bin",
        "System Volume Information",
        "pagefile.sys",
        "hiberfil.sys",
        "swapfile.sys",
    };

    /// <summary>
    /// True when <paramref name="path"/> is one of <see cref="RootNames"/> in a
    /// volume root, or lives underneath one. Such a path is never user data, so it
    /// must never be backed up.
    /// </summary>
    /// <remarks>
    /// Deliberately matches on the FIRST path component after the volume root
    /// only. A user directory genuinely named "$Extend" or "$Recycle.Bin" nested
    /// somewhere else (e.g. <c>D:\projects\$Extend</c>) is ordinary data, is
    /// enumerable, is visible in the treeview, and must keep being backed up.
    /// </remarks>
    public static bool IsVolumeMetadata(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        string name = FirstComponentAfterRoot(path);
        return name.Length > 0 && RootNames.Contains(name);
    }

    /// <summary>
    /// The first path component below the volume root, or "" when the path has
    /// none (it IS a root), isn't a rooted local path, or cannot be one of
    /// <see cref="RootNames"/>.
    /// </summary>
    /// <remarks>
    /// Written against the raw string rather than <see cref="Path.GetPathRoot"/>
    /// + splitting because this runs per candidate path on the continuous-backup
    /// hot path, and because the input can be a USN-resolved path that has not
    /// been normalised yet.
    /// </remarks>
    private static string FirstComponentAfterRoot(string path)
    {
        int start;

        // UNC: \\server\share\<first>...  (also covers \\?\UNC\ after the
        // extended prefix is stripped below).
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            int p = path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? 8
                  : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? 4
                  : 2;

            // For \\?\C:\... the remainder is a normal drive path.
            if (p == 4 && p + 1 < path.Length && path[p + 1] == ':')
            {
                start = p + 2;
            }
            else
            {
                // Skip the server and share components.
                int seps = 0;
                while (p < path.Length && seps < 2)
                {
                    if (path[p] is '\\' or '/')
                        seps++;
                    p++;
                }
                if (seps < 2)
                    return string.Empty;
                start = p;
                return ComponentAt(path, start);
            }
        }
        else if (path.Length >= 2 && path[1] == ':')
        {
            start = 2;
        }
        else
        {
            return string.Empty;
        }

        return ComponentAt(path, start);
    }

    private static string ComponentAt(string path, int start)
    {
        while (start < path.Length && (path[start] is '\\' or '/'))
            start++;
        if (start >= path.Length)
            return string.Empty;

        // Fast reject: every name in RootNames starts with '$', 's', 'p' or 'h',
        // so an ordinary path usually costs one character comparison and no
        // allocation.
        if (path[start] is not ('$' or 's' or 'S' or 'p' or 'P' or 'h' or 'H'))
            return string.Empty;

        int end = start;
        while (end < path.Length && path[end] is not ('\\' or '/'))
            end++;

        return path[start..end];
    }
}
