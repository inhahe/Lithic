namespace LithicBackup.Core.Models;

/// <summary>
/// Resume point for reading an NTFS volume's USN change journal.
/// </summary>
/// <param name="VolumeId">Volume root, e.g. <c>"C:\"</c>.</param>
/// <param name="JournalId">
/// Identity of the journal the cursor belongs to. If the volume's journal is
/// deleted and re-created the identity changes, signalling that
/// <see cref="NextUsn"/> is no longer valid and a fresh start is required.
/// </param>
/// <param name="NextUsn">The next USN to read from on the volume.</param>
/// <param name="UpdatedUtc">When this cursor was last persisted.</param>
public readonly record struct UsnCursor(
    string VolumeId,
    long JournalId,
    long NextUsn,
    DateTime UpdatedUtc);
