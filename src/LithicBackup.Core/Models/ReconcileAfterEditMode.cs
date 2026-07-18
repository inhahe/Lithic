namespace LithicBackup.Core.Models;

/// <summary>
/// What happens after you edit a backup set's source selection and save: whether
/// the app reconciles the destination with the change — offering to back up
/// folders you newly added and purge copies of folders you removed. Reconciling
/// requires scanning the affected folders, so a large edit can mean a long scan.
/// The reconcile only ever runs when a checkbox (or auto-include-new) was actually
/// toggled; merely browsing/expanding the tree never triggers it, regardless of
/// this setting.
/// </summary>
public enum ReconcileAfterEditMode
{
    /// <summary>
    /// After a change, ask the user whether to scan and reconcile now. Default.
    /// Chosen as the first (zero) value so a missing/older settings file defaults
    /// here.
    /// </summary>
    Ask,

    /// <summary>
    /// Always reconcile automatically after a change, with no prompt.
    /// </summary>
    Always,

    /// <summary>
    /// Never reconcile after an edit; added/removed folders sync on the next full
    /// backup of the set instead.
    /// </summary>
    Never,
}
