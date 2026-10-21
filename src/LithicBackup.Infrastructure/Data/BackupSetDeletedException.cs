namespace LithicBackup.Infrastructure.Data;

/// <summary>
/// A call reached a backup set that has been deleted: its database is closed and
/// gone, and is not re-created for a reader that held on to it.
/// </summary>
internal sealed class BackupSetDeletedException(int setId)
    : InvalidOperationException($"Backup set {setId} has been deleted.")
{
    public int SetId { get; } = setId;
}
