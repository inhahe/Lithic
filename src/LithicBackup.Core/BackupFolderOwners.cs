using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.Core;

/// <summary>
/// Which set has a backup folder. A folder belongs to one set
/// (<see cref="JobOptions.SharesBackupFolderWith"/>): the app refuses to give a
/// set another's, and the app and the background service work in no folder two
/// sets already share.
/// </summary>
public static class BackupFolderOwners
{
    /// <summary>
    /// The name of a set other than <paramref name="setId"/> whose backup folder
    /// <paramref name="options"/> would share, or null when none.
    /// </summary>
    /// <param name="sets">Every set's options (<see cref="ICatalogRepository.GetAllBackupSetOptionsAsync"/>).</param>
    /// <param name="setId">The set the options are for; 0 for one not created yet.</param>
    /// <param name="options">Its options as they are, or as they would be saved.</param>
    public static string? FindOther(IEnumerable<BackupSetOptions> sets, int setId, JobOptions options)
    {
        foreach (var other in sets)
        {
            if (other.Id != setId && other.JobOptions is { } theirs && options.SharesBackupFolderWith(theirs))
                return other.Name;
        }
        return null;
    }

    /// <summary>
    /// What to tell the user about a set that shares its backup folder with
    /// <paramref name="partner"/>: what happens meanwhile and the ways out, as
    /// sentences to follow the place's own account. One text for the app, the
    /// background service's log and Cleanup.
    /// </summary>
    public static string SharedAdvice(string partner) =>
        "Until one of the two has a folder of its own, neither is backed up and nothing is deleted from "
        + $"that folder. If one set is a copy of the other, delete the copy. Otherwise clear one set's backup "
        + "history in its editor, give it a destination of its own, and back it up afresh; the other set's "
        + $"Cleanup can then remove the copies it left behind. (The other set is \"{partner}\".)";

    /// <summary><see cref="FindOther"/>, reading the sets' options from the catalog.</summary>
    public static async Task<string?> FindOtherAsync(
        ICatalogRepository catalog, int setId, JobOptions options, CancellationToken ct = default)
        => FindOther(await catalog.GetAllBackupSetOptionsAsync(ct).ConfigureAwait(false), setId, options);
}
