namespace LithicBackup.Core.Models;

/// <summary>
/// One direct child of a directory in the lazy restore browser: either an
/// immediate subdirectory or a file that lives directly under the parent
/// directory. Produced by the catalog's loose-index skip-scan so a directory's
/// children can be listed on expand without loading its whole subtree.
/// </summary>
/// <param name="Name">The child's own name (last path segment).</param>
/// <param name="FullPath">
/// The child's full source path (e.g. <c>D:\docs\sub</c> for a directory or
/// <c>D:\docs\a.txt</c> for a file). For a directory this is the prefix used to
/// list <em>its</em> children (with a trailing separator appended).
/// </param>
/// <param name="IsDirectory">True for a subdirectory, false for a file leaf.</param>
/// <param name="SizeBytes">
/// The file's latest-version size, in bytes. Zero for directories (their
/// aggregate size is not computed up front in the lazy browser).
/// </param>
/// <param name="BackedUpUtc">
/// When the file's latest version was backed up. Null for directories.
/// </param>
public readonly record struct RestoreTreeChild(
    string Name,
    string FullPath,
    bool IsDirectory,
    long SizeBytes,
    DateTime? BackedUpUtc);
