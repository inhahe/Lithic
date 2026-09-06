using System.Windows;
using LithicBackup.ViewModels;
using LithicBackup.Views;

namespace LithicBackup.Services;

/// <summary>
/// What a task flow does to a backup set's data. Drives the conflict rules in
/// <see cref="TaskWindowManager"/>, which are the price of letting several task
/// windows be open at once: two windows looking at one set is usually fine, two
/// windows CHANGING one set is not.
/// </summary>
/// <param name="Id">Stable key for "same task", e.g. <c>cleanup</c>.</param>
/// <param name="Title">Window title prefix shown to the user.</param>
/// <param name="WritesCatalog">Can mark catalog rows deleted / rewrite rows.</param>
/// <param name="WritesDestination">Can delete or rewrite files on the destination.</param>
/// <param name="ReadsDestination">Reads backed-up content, so is broken by a concurrent writer.</param>
public sealed record TaskKind(
    string Id,
    string Title,
    bool WritesCatalog = false,
    bool WritesDestination = false,
    bool ReadsDestination = false);

/// <summary>The task flows that get their own window.</summary>
public static class TaskKinds
{
    /// <summary>Marks rows deleted AND deletes destination files. The heaviest writer.</summary>
    public static readonly TaskKind Cleanup =
        new("cleanup", "Cleanup", WritesCatalog: true, WritesDestination: true, ReadsDestination: true);

    /// <summary>Reads destination content to hash it; its Repair action flags rows for re-backup.</summary>
    public static readonly TaskKind Verify =
        new("verify", "Verify Integrity", WritesCatalog: true, ReadsDestination: true);

    /// <summary>Reads backed-up content and writes it back out to the filesystem.</summary>
    public static readonly TaskKind Restore =
        new("restore", "Restore", ReadsDestination: true);

    /// <summary>Reads a backup folder directly; not tied to a set.</summary>
    public static readonly TaskKind CatalogFreeRestore =
        new("catalogfree", "Catalog-free Restore", ReadsDestination: true);

    /// <summary>Reads the optical disc, and can re-burn repairs to it.</summary>
    public static readonly TaskKind TestDisc =
        new("testdisc", "Test Disc", WritesDestination: true, ReadsDestination: true);

    /// <summary>Catalog + source reads only.</summary>
    public static readonly TaskKind Coverage = new("coverage", "Backup Coverage");

    /// <summary>Source scan only.</summary>
    public static readonly TaskKind LargestFiles = new("largest", "Largest Files");

    /// <summary>Cross-set catalog search; read-only and not tied to one set.</summary>
    public static readonly TaskKind FindFile = new("findfile", "Find File");
}

/// <summary>
/// Opens, tracks and focuses the non-modal task windows, and enforces the rules
/// about what may be open at the same time on one backup set.
///
/// <para><b>Why this exists.</b> Task flows used to be swapped into the main
/// window's <c>CurrentView</c>, which meant anything that set <c>CurrentView</c>
/// destroyed whatever the user was doing. A backup starting did exactly that
/// (<c>StartBurn</c> reset the view so the row's progress panel was visible), so
/// leaving a multi-hour Cleanup scan running and starting a backup silently
/// threw the scan away. A task that owns its own window cannot be discarded by
/// an unrelated part of the app.</para>
/// </summary>
public sealed class TaskWindowManager
{
    private readonly Dictionary<string, TaskWindow> _open = new(StringComparer.Ordinal);
    private readonly Func<int, bool> _isBackupRunning;
    private readonly Func<Window?> _mainWindow;

    /// <summary>Cascade offset so successive windows don't land exactly on top of each other.</summary>
    private int _cascadeStep;

    public TaskWindowManager(Func<int, bool> isBackupRunning, Func<Window?> mainWindow)
    {
        _isBackupRunning = isBackupRunning;
        _mainWindow = mainWindow;
    }

    /// <summary>
    /// Focus an already-open window for this task+set without building anything.
    ///
    /// <para>For callers whose view model is expensive to construct - Largest
    /// Files starts a full source scan the moment it exists - asking first is
    /// the difference between focusing a window and silently starting a second
    /// scan whose results are thrown away.</para>
    /// </summary>
    /// <returns>true if a window existed and was focused.</returns>
    public bool TryFocusExisting(TaskKind kind, int? setId)
    {
        if (!_open.TryGetValue(KeyFor(kind, setId), out var existing))
            return false;

        if (existing.WindowState == WindowState.Minimized)
            existing.WindowState = WindowState.Normal;
        existing.Activate();
        return true;
    }

    /// <summary>Kind + set identity of an open window, for the conflict check.</summary>
    private readonly Dictionary<string, (TaskKind Kind, int? SetId)> _meta = new(StringComparer.Ordinal);

    private static string KeyFor(TaskKind kind, int? setId) =>
        setId.HasValue ? $"{kind.Id}:{setId.Value}" : kind.Id;

    /// <summary>
    /// Show a task flow in its own window.
    ///
    /// <list type="bullet">
    /// <item>The same task already open on the same set just gets focused —
    /// never a second window, because two Cleanups purging one catalog is not
    /// something the catalog is built for.</item>
    /// <item>A DIFFERENT task on the same set is allowed, but if the pair can
    /// interfere the user is told exactly which combination and asked to
    /// confirm.</item>
    /// </list>
    /// </summary>
    /// <param name="subscribeDone">
    /// Hooks the flow's own <c>DoneRequested</c> event to the window's Close.
    /// Passed in by the caller because each flow view model declares that event
    /// itself rather than inheriting it.
    /// </param>
    /// <returns>The window, or null if the user declined the conflict warning.</returns>
    public TaskWindow? Show(
        TaskKind kind,
        int? setId,
        string? setName,
        Func<ViewModelBase> createFlow,
        Action<ViewModelBase, Action> subscribeDone)
    {
        string key = KeyFor(kind, setId);

        // Already open: focus it rather than starting the work twice.
        if (_open.TryGetValue(key, out var existing))
        {
            if (existing.WindowState == WindowState.Minimized)
                existing.WindowState = WindowState.Normal;
            existing.Activate();
            return existing;
        }

        if (setId.HasValue && !ConfirmNoConflict(kind, setId.Value, setName))
            return null;

        var flow = createFlow();
        var window = new TaskWindow
        {
            Flow = flow,
            WindowTitle = setName is null
                ? $"{kind.Title} — Lithic Backup"
                : $"{kind.Title} — {setName}",
        };

        PlaceCascaded(window);

        subscribeDone(flow, () => window.Close());

        window.Closed += (_, _) =>
        {
            _open.Remove(key);
            _meta.Remove(key);
        };

        _open[key] = window;
        _meta[key] = (kind, setId);

        window.Show();
        return window;
    }

    /// <summary>
    /// Warn about combinations that can genuinely interfere on one set, and let
    /// the user decide. Returns false only if they choose not to proceed.
    /// </summary>
    private bool ConfirmNoConflict(TaskKind kind, int setId, string? setName)
    {
        var reasons = new List<string>();

        // A running backup is a writer too, even though it has no window.
        if ((kind.WritesCatalog || kind.WritesDestination || kind.ReadsDestination)
            && _isBackupRunning(setId))
        {
            reasons.Add(
                "a backup of this set is running right now, so it is actively "
                + "writing the catalog and the destination");
        }

        foreach (var (openKind, openSetId) in _meta.Values)
        {
            if (openSetId != setId)
                continue;

            // Two writers of the catalog can undo each other's bookkeeping.
            if (kind.WritesCatalog && openKind.WritesCatalog)
                reasons.Add($"“{openKind.Title}” is open on this set and can also change catalog records");

            // A writer of destination files versus anything reading them.
            else if (kind.WritesDestination && (openKind.WritesDestination || openKind.ReadsDestination))
                reasons.Add($"“{openKind.Title}” is open on this set and reads backed-up files this task can delete");
            else if (kind.ReadsDestination && openKind.WritesDestination)
                reasons.Add($"“{openKind.Title}” is open on this set and can delete backed-up files this task needs");
        }

        if (reasons.Count == 0)
            return true;

        string name = setName is null ? "this backup set" : $"“{setName}”";
        string detail = string.Join("\n• ", reasons);

        var answer = MessageBox.Show(
            $"Opening {kind.Title} for {name} at the same time as other work on the "
            + "same set can produce results that disagree with each other:\n\n• "
            + detail
            + "\n\nThe safest order is to finish one before starting the other.\n\n"
            + "Open it anyway?",
            $"{kind.Title} — possible conflict",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        return answer == MessageBoxResult.Yes;
    }

    /// <summary>
    /// Offset each new window slightly so a second one is visibly a second one.
    /// Anchored to the main window when there is one, else to the screen.
    /// </summary>
    private void PlaceCascaded(Window window)
    {
        var anchor = _mainWindow();
        double baseLeft = anchor?.Left ?? 80;
        double baseTop = anchor?.Top ?? 80;

        const int stepPx = 28;
        const int wrapAfter = 6;

        int step = _cascadeStep % wrapAfter;
        _cascadeStep++;

        window.Left = baseLeft + 40 + step * stepPx;
        window.Top = baseTop + 40 + step * stepPx;
    }

    /// <summary>
    /// Close every task window. Called when the main window closes, so task
    /// windows never outlive the app and strand it with no visible UI (the app
    /// uses ShutdownMode.OnExplicitShutdown).
    /// </summary>
    public void CloseAll()
    {
        foreach (var window in _open.Values.ToList())
        {
            try { window.Close(); }
            catch { /* a window already closing is not an error */ }
        }
        _open.Clear();
        _meta.Clear();
    }
}
