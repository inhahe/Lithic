using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LithicBackup.Core;

/// <summary>
/// Keeps a walk of a source tree from following a directory link (junction,
/// symbolic link, mounted folder) into itself. One per walk.
/// </summary>
/// <remarks>
/// <para>A walk is a depth-first search of a directed graph: directories are
/// nodes, and each subdirectory entry is an edge, a link's to wherever it
/// points. Without links the graph is a tree. A link whose target is a directory
/// on the walk's current path - the folder holding it, or one above that - is a
/// back edge: following it re-enters that path, and from there reaches the same
/// link again, without end (<c>C:\a\b\loop</c> pointing to <c>C:\a</c> yields
/// <c>C:\a\b\loop\b\loop\b\...</c> until the path is too long). Such a link is
/// not followed. Every other link is: its files are backed up under the link's
/// path, as before, including a link to a folder that is also selected
/// elsewhere, which is finite.</para>
///
/// <para><b>What "on the path" means.</b> Directories are compared by their
/// final path (<c>GetFinalPathNameByHandle</c>: links resolved, long names, the
/// volume's own drive letter), so a <c>subst</c> drive, a mounted folder or a
/// link reached through another link is still seen for what it is. The current
/// path is split by the links it went through; within each stretch the folders
/// are one chain of real directories, so a target above any of them is above
/// the deepest, and only that one's final path is needed: the holding folder's,
/// recorded when each link is followed, and the current folder's. So plain
/// folders cost nothing, and a link costs two handle opens (one, when it points
/// below itself, as a cloud-files folder does).</para>
///
/// <para>Paths are the walk's own (as enumerated, below its start), and the
/// guard does not depend on the order the walk visits them, so it serves
/// recursive walks and .NET's queue-based enumeration alike.</para>
/// </remarks>
public sealed class LinkLoopGuard
{
    /// <summary>
    /// The links this walk has followed, by the path the walk reached each by,
    /// with the final path of the folder holding each: the deepest real folder
    /// of the stretch of the walk's path above it.
    /// </summary>
    private readonly Dictionary<string, string> _followed = new(StringComparer.OrdinalIgnoreCase);

    // The last holding folder resolved: the links in one folder share it.
    private string? _lastFolder, _lastFolderFinal;

    /// <summary>
    /// Every file under <paramref name="directory"/>, at any depth, following
    /// links but not into themselves. A folder <paramref name="prune"/> returns
    /// true for is not entered, and an unreadable one is skipped.
    /// </summary>
    public static IEnumerable<string> EnumerateFiles(string directory, Func<string, bool>? prune = null)
    {
        var links = new LinkLoopGuard();
        links.Seed(directory);
        return new System.IO.Enumeration.FileSystemEnumerable<string>(
            directory, (ref System.IO.Enumeration.FileSystemEntry entry) => entry.ToFullPath(),
            new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 })
        {
            ShouldIncludePredicate = (ref System.IO.Enumeration.FileSystemEntry entry) => !entry.IsDirectory,
            ShouldRecursePredicate = (ref System.IO.Enumeration.FileSystemEntry entry) =>
            {
                string path = entry.ToFullPath();
                return (prune is null || !prune(path)) && links.MayEnter(path, entry.Attributes);
            },
        };
    }

    /// <summary>
    /// Record the links among <paramref name="startDirectory"/> and its ancestors,
    /// so a loop back to above the place a walk starts is seen too (a selected
    /// folder inside a junction, say). Call it for each place a walk starts.
    /// </summary>
    public void Seed(string startDirectory)
    {
        string? dir = Trim(startDirectory);
        while (dir is not null)
        {
            string? parent = Path.GetDirectoryName(dir);
            if (parent is null)
                break;
            try
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                    _followed[dir] = FinalPathOfFolder(Trim(parent)!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            dir = parent;
        }
    }

    /// <summary>
    /// Whether the walk may descend into the subdirectory at
    /// <paramref name="path"/>, given its <paramref name="attributes"/> as
    /// enumerated: always for a plain folder, and for a link unless it points
    /// back to the walk's current path (see the class remarks).
    /// </summary>
    public bool MayEnter(string path, FileAttributes attributes) =>
        (attributes & FileAttributes.ReparsePoint) == 0 || MayFollow(path);

    /// <summary>
    /// Whether to follow the directory link at <paramref name="link"/>: false
    /// when its target is the folder holding it or a folder above that on the
    /// walk's path. A link it cannot resolve is followed; one that is broken
    /// fails to enumerate, as before.
    /// </summary>
    public bool MayFollow(string link)
    {
        link = Trim(link)!;
        string? target = FinalPath(link);
        string? folder = Path.GetDirectoryName(link);
        if (target is null || folder is null)
            return true;

        // A target at or below the link's own path cannot be above it.
        if (!IsAtOrUnder(target, link))
        {
            folder = Trim(folder)!;
            if (IsAtOrUnder(FinalPathOfFolder(folder), target))
                return false;
            for (string? up = folder; up is not null; up = Path.GetDirectoryName(up))
            {
                if (_followed.TryGetValue(Trim(up)!, out string? holder) && IsAtOrUnder(holder, target))
                    return false;
            }
        }

        _followed[link] = FinalPathOfFolder(Trim(folder)!);
        return true;
    }

    /// <summary>The final path of <paramref name="folder"/>, or the path itself if it cannot be resolved.</summary>
    private string FinalPathOfFolder(string folder)
    {
        if (!string.Equals(folder, _lastFolder, StringComparison.OrdinalIgnoreCase))
        {
            _lastFolder = folder;
            _lastFolderFinal = FinalPath(folder) ?? folder;
        }
        return _lastFolderFinal!;
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="ancestor"/> or lies under it.</summary>
    private static bool IsAtOrUnder(string path, string ancestor) =>
        path.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase)
        && (path.Length == ancestor.Length || path[ancestor.Length] == Path.DirectorySeparatorChar);

    /// <summary>A path without a trailing separator, so a drive root is "C:" and its children "C:\x".</summary>
    private static string? Trim(string? path) => path?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    /// <summary>
    /// The final path of the directory at <paramref name="path"/> - every link
    /// on the way resolved - in the form the walk's paths use (<c>C:\x</c>,
    /// <c>\\server\share\x</c>), without a trailing separator; null if it
    /// cannot be opened.
    /// </summary>
    internal static string? FinalPath(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        string open = full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full
            : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..]
            : @"\\?\" + full;

        using var handle = CreateFileW(open, FileReadAttributes, FileShareAll, IntPtr.Zero,
            OpenExisting, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid)
            return null;

        var buffer = new char[520];
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (length >= buffer.Length)
        {
            buffer = new char[length + 1];
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        }
        if (length == 0 || length >= buffer.Length)
            return null;

        string final = new(buffer, 0, (int)length);
        if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            final = @"\" + final[7..];
        else if (final.StartsWith(@"\\?\", StringComparison.Ordinal))
            final = final[4..];
        return Trim(final);
    }

    private const uint FileReadAttributes = 0x80;
    private const uint FileShareAll = 0x7;             // read | write | delete
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;   // needed to open a directory

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file, [Out] char[] path, uint pathLength, uint flags);
}
