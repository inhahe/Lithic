using LithicBackup.Core.Models;

namespace LithicBackup.Core;

/// <summary>
/// What to tell the user when some of a set's live records belong to another
/// folder than its backup folder
/// (<see cref="Interfaces.ICatalogRepository.FindLiveRecordsOutsideAsync"/>). One
/// text for every place that refuses such a set - the app, the background
/// service's log and Cleanup - so that they give the same way out.
/// </summary>
public static class RecordsElsewhere
{
    /// <summary>
    /// The ways out, as sentences to follow the place's own account of the refusal.
    /// </summary>
    /// <param name="elsewhere">The folder the records were written to (a disc label).</param>
    /// <param name="backupRoot">The set's backup folder now.</param>
    public static string Advice(string elsewhere, string backupRoot)
    {
        const string General =
            "If you moved the backup to another drive or folder, use Change Destination on the set's "
            + "right-click menu. If you changed its \"Create subdirectory\" setting or the subfolder's name, "
            + "change it back. To start afresh in the new folder, clear the set's backup history in its editor.";

        // The folder just above a subfolder: either "Create subdirectory" was
        // turned on after backing up, or the set has backups in both places,
        // which only the background service before 1.18 made (it ignored the
        // subfolder). Neither Change Destination nor the setting reunites those.
        if (System.IO.Path.GetDirectoryName(backupRoot.TrimEnd('\\', '/')) is { } above
            && JobOptions.SameFolderOnAnyDrive(elsewhere, above))
            return "If you turned on \"Create subdirectory\" after backing the set up, turn it off again. "
                   + "If the set has backups both there and in its subfolder (LithicBackup's background service "
                   + "before version 1.18 ignored the subfolder), neither that nor Change Destination brings them "
                   + "together: clear the set's backup history in its editor, then seed it from its backup folder. "
                   + "The older copies beside the subfolder then belong to no set; check that no other set backs "
                   + $"up to {elsewhere} before deleting them by hand.";
        return General;
    }
}
