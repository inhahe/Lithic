namespace LithicBackup.Core.Models;

/// <summary>
/// JSON schema for .fileref manifest files. A .fileref stores no bytes of its
/// own; it marks a file whose content is byte-identical to another file already
/// backed up. The content lives as a plain, normally-named file elsewhere in the
/// backup tree, and is located by <see cref="Hash"/> via the catalog at restore
/// and verify time.
///
/// The on-disk .fileref files are not needed by Lithic's own restore (which
/// resolves by <see cref="Hash"/> through the catalog). They exist so the
/// destination tree is self-describing: a human can inspect what a reference
/// points at, and a catalog-free restore (when the catalog is lost) can follow
/// <see cref="ContentPath"/> to the bytes.
/// </summary>
public class FileRefManifest
{
    /// <summary>Original filename (without path).</summary>
    public string OriginalName { get; init; } = "";

    /// <summary>Original file size in bytes.</summary>
    public long OriginalSize { get; init; }

    /// <summary>SHA-256 hash of the original file contents. The authoritative
    /// anchor: Lithic's catalog restore resolves references by this hash, and a
    /// catalog-free restore verifies <see cref="ContentPath"/> against it.</summary>
    public string Hash { get; init; } = "";

    /// <summary>
    /// Full source path this reference was backed up from (e.g.
    /// <c>C:\Users\me\dup.txt</c>). Purely informational, for human inspection.
    /// For a previous-version reference this is the source path as it was at that
    /// version; the live source file at this path may now hold newer content.
    /// </summary>
    public string SourcePath { get; init; } = "";

    /// <summary>
    /// Destination-relative path of the plain copy that holds the actual bytes
    /// for this content (e.g. <c>C/Users/me/orig.txt</c>). A best-effort HINT,
    /// not the source of truth: it is maintained as plain copies move (eviction,
    /// retention promotion) but may go stale after a crash. Catalog-free restore
    /// follows it, verifies the bytes against <see cref="Hash"/>, and falls back
    /// to a content-hash scan of the tree if the hint is missing or stale.
    /// </summary>
    public string ContentPath { get; set; } = "";
}
