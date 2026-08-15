namespace LithicBackup.Core;

/// <summary>
/// Paths that live on a volume but are NOT user data and can never be shown in
/// a directory listing — NTFS's own metadata files and directories.
///
/// <para><b>Why this exists.</b> Two different mechanisms discover files for a
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
/// <para><b>The invariant.</b> Anything the selection treeview cannot display
/// must not be backed up, so that what the user sees is what the backup does.
/// This class is the single definition of that set, applied both to the backup
/// hot paths and to the worker's set-membership / auto-include-new test, so the
/// two can never drift apart.</para>
/// </summary>
public static class VolumeMetadataPaths
{
    /// <summary>
    /// The NTFS metadata entries that sit directly in a volume root. All of them
    /// begin with '$', and none are returned by directory enumeration.
    ///
    /// <para><c>$Extend</c> is the one that actually shows up in USN records in
    /// practice (via <c>$Deleted</c>, <c>$RmMetadata</c>, <c>$Quota</c>,
    /// <c>$ObjId</c>, <c>$Reparse</c>). The rest are listed so a future Windows
    /// build that starts journaling them is covered too, rather than silently
    /// reintroducing this bug.</para>
    /// </summary>
    private static readonly HashSet<string> RootMetadataNames = new(StringComparer.OrdinalIgnoreCase)
    {
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
    };

    /// <summary>
    /// True when <paramref name="path"/> is an NTFS metadata entry in a volume
    /// root, or lives underneath one. Such a path is never user data and is never
    /// enumerable, so it must never be backed up.
    /// </summary>
    /// <remarks>
    /// Deliberately matches on the FIRST path component after the volume root
    /// only. A user directory genuinely named "$Extend" nested somewhere else
    /// (e.g. <c>D:\projects\$Extend</c>) is ordinary data, is enumerable, is
    /// visible in the treeview, and must keep being backed up.
    /// </remarks>
    public static bool IsVolumeMetadata(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        string name = FirstComponentAfterRoot(path);
        return name.Length > 0 && RootMetadataNames.Contains(name);
    }

    /// <summary>
    /// The first path component below the volume root, or "" when the path has
    /// none (it IS a root) or isn't a rooted local path.
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

        // Fast reject: every NTFS metadata name starts with '$', so a normal
        // path costs one character comparison and no allocation.
        if (path[start] != '$')
            return string.Empty;

        int end = start;
        while (end < path.Length && path[end] is not ('\\' or '/'))
            end++;

        return path[start..end];
    }
}
