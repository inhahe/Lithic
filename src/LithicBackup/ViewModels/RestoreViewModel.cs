using System.Collections.ObjectModel;
using System.Windows.Input;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.ViewModels;

/// <summary>
/// ViewModel for the file restore view. Browses a backup set's files as a lazy
/// tree — a directory's direct children are read from the catalog only when it is
/// expanded — and restores only the selected subset, materialised on demand. The
/// whole catalog is never loaded up front, so opening the restore dialog is fast
/// even on sets with hundreds of thousands of files.
/// </summary>
public class RestoreViewModel : ViewModelBase
{
    private readonly ICatalogRepository _catalog;
    private readonly IRestoreService _restoreService;

    private BackupSet? _selectedBackupSet;
    private bool _restoreToOriginal;
    private HashSet<string> _selectedDriveKeys = new(StringComparer.OrdinalIgnoreCase);
    private bool _hasSelection;
    private bool _isLoading;
    private bool _isRestoring;
    private bool _isComplete;
    private string _statusText = "Select a backup set to see available files.";
    private string _resultDetail = "";
    private double _restorePercentage;
    private string _currentFile = "";
    private string _progressText = "";
    private CancellationTokenSource? _cts;

    // Coalesces the async selected-file-count recomputation across rapid toggles.
    private CancellationTokenSource? _countCts;
    // Per-directory subtree file-count cache (prefix -> count) so recounting a
    // stable selection doesn't re-query the catalog.
    private readonly Dictionary<string, int> _subtreeCountCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fired when the user clicks "Done" or "Close".</summary>
    public event Action? DoneRequested;

    public RestoreViewModel(
        ICatalogRepository catalog,
        IRestoreService restoreService,
        BackupSet backupSet)
    {
        _catalog = catalog;
        _restoreService = restoreService;
        _selectedBackupSet = backupSet;
        Roots = [];
        DriveDestinations = [];

        RestoreCommand = new RelayCommand(_ => _ = RestoreAsync(), _ => CanRestore());
        CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsRestoring);
        DoneCommand = new RelayCommand(_ => DoneRequested?.Invoke());

        _ = LoadRootsAsync(backupSet.Id);
    }

    // --- Properties ---

    public BackupSet SelectedBackupSet => _selectedBackupSet!;

    /// <summary>Root nodes of the restorable-file tree (one per source drive).</summary>
    public ObservableCollection<RestoreNodeViewModel> Roots { get; }

    /// <summary>Number of files currently selected for restore (computed on demand).</summary>
    public int SelectedFileCount
    {
        get => _selectedFileCount;
        private set => SetProperty(ref _selectedFileCount, value);
    }
    private int _selectedFileCount;

    /// <summary>
    /// Tristate "check all" bound to the header checkbox: true when every root
    /// is fully selected, false when none are, null when partial.
    /// </summary>
    public bool? IsAllSelected
    {
        get
        {
            if (Roots.Count == 0)
                return false;
            bool allTrue = Roots.All(r => r.IsSelected == true);
            bool allFalse = Roots.All(r => r.IsSelected == false);
            return allTrue ? true : (allFalse ? false : (bool?)null);
        }
        set
        {
            bool target = value == true;
            foreach (var root in Roots)
                root.IsSelected = target;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// One row per distinct source drive in the backup set, each with its own
    /// editable destination folder.
    /// </summary>
    public ObservableCollection<DriveDestinationViewModel> DriveDestinations { get; }

    /// <summary>
    /// When true, each drive's files are restored to their original locations
    /// (the drive's own root) and the per-drive destination inputs are ignored.
    /// </summary>
    public bool RestoreToOriginal
    {
        get => _restoreToOriginal;
        set
        {
            if (SetProperty(ref _restoreToOriginal, value))
            {
                OnPropertyChanged(nameof(IsCustomDestination));
                CommandManager.InvalidateRequerySuggested();
            }
        }
    }

    /// <summary>Inverse of <see cref="RestoreToOriginal"/>, for enabling the per-drive inputs.</summary>
    public bool IsCustomDestination => !_restoreToOriginal;

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    /// <summary>
    /// Live description of what the initial load is doing, shown in the loading
    /// overlay. The lazy tree only reads the drive roots up front, so this is
    /// brief — the whole catalog is no longer scanned to open the dialog.
    /// </summary>
    public string LoadProgressText
    {
        get => _loadProgressText;
        set => SetProperty(ref _loadProgressText, value);
    }
    private string _loadProgressText = "";

    public bool IsRestoring
    {
        get => _isRestoring;
        set => SetProperty(ref _isRestoring, value);
    }

    public bool IsComplete
    {
        get => _isComplete;
        set => SetProperty(ref _isComplete, value);
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    public string ResultDetail
    {
        get => _resultDetail;
        set => SetProperty(ref _resultDetail, value);
    }

    public double RestorePercentage
    {
        get => _restorePercentage;
        set => SetProperty(ref _restorePercentage, value);
    }

    public string CurrentFile
    {
        get => _currentFile;
        set => SetProperty(ref _currentFile, value);
    }

    public string ProgressText
    {
        get => _progressText;
        set => SetProperty(ref _progressText, value);
    }

    // --- Commands ---

    public ICommand RestoreCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand DoneCommand { get; }

    // --- Logic ---

    /// <summary>
    /// Load only the top-level children (drive roots) of the set. Everything
    /// below is fetched lazily on expand.
    /// </summary>
    private async Task LoadRootsAsync(int backupSetId)
    {
        IsLoading = true;
        StatusText = "Loading files from catalog...";
        LoadProgressText = "Reading drives…";
        Roots.Clear();
        SelectedFileCount = 0;

        try
        {
            // Run off the UI thread: the set DB's async lock completes synchronously
            // when uncontended, so the skip-scan's index seeks would otherwise run on
            // the caller (UI) thread. See the restore-freeze note in known-issues.md.
            var rootChildren = await Task.Run(
                () => _catalog.GetRestoreChildrenAsync(backupSetId, ""));

            var driveLetters = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var child in rootChildren)
            {
                var node = new RestoreNodeViewModel(
                    child.Name, child.FullPath, 0, child.IsDirectory,
                    child.SizeBytes, child.BackedUpUtc,
                    LoadChildrenFromCatalog, OnNodeSelectionChanged);
                Roots.Add(node);
                driveLetters.Add(GetDriveLetter(child.FullPath));
            }

            DriveDestinations.Clear();
            foreach (var drive in driveLetters)
            {
                var driveVm = new DriveDestinationViewModel(drive);
                driveVm.PropertyChanged += (_, _) =>
                    CommandManager.InvalidateRequerySuggested();
                DriveDestinations.Add(driveVm);
            }

            // Expand the top level so the tree isn't a wall of collapsed drive rows.
            foreach (var root in Roots)
                if (root.IsDirectory)
                    root.IsExpanded = true;

            OnPropertyChanged(nameof(IsAllSelected));
            StatusText = Roots.Count == 0
                ? "No files available for restore."
                : "Select files to restore.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load files: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            LoadProgressText = "";
        }
    }

    /// <summary>
    /// Catalog child-listing delegate handed to every tree node. Runs off the UI
    /// thread (<see cref="Task.Run(Func{Task{TResult}})"/>) because the set DB's
    /// async lock completes synchronously when uncontended, so a directory's skip-
    /// scan seeks would otherwise run on the UI thread.
    /// </summary>
    private Task<IReadOnlyList<RestoreTreeChild>> LoadChildrenFromCatalog(
        string prefix, CancellationToken ct) =>
        Task.Run(() => _catalog.GetRestoreChildrenAsync(_selectedBackupSet!.Id, prefix, ct), ct);

    private void OnNodeSelectionChanged()
    {
        var dirPrefixes = new List<string>();
        var filePaths = new List<string>();
        foreach (var root in Roots)
            root.CollectSelection(dirPrefixes, filePaths);

        _hasSelection = dirPrefixes.Count > 0 || filePaths.Count > 0;

        // Drive keys are derivable synchronously from the selected paths.
        _selectedDriveKeys = dirPrefixes.Concat(filePaths)
            .Select(GetDriveKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        OnPropertyChanged(nameof(IsAllSelected));
        CommandManager.InvalidateRequerySuggested();

        // The exact file count needs a catalog query per fully-selected directory,
        // so compute it asynchronously (coalescing rapid toggles) rather than
        // blocking the checkbox click.
        _ = RecomputeSelectedCountAsync(dirPrefixes, filePaths);
    }

    private async Task RecomputeSelectedCountAsync(
        List<string> dirPrefixes, List<string> filePaths)
    {
        _countCts?.Cancel();
        var cts = new CancellationTokenSource();
        _countCts = cts;
        var ct = cts.Token;

        try
        {
            int total = filePaths.Count;
            foreach (var prefix in dirPrefixes)
            {
                ct.ThrowIfCancellationRequested();
                if (!_subtreeCountCache.TryGetValue(prefix, out int count))
                {
                    // Off the UI thread (uncontended set-DB lock completes sync).
                    var (fileCount, _) = await Task.Run(
                        () => _catalog.GetActiveSubtreeStatsAsync(_selectedBackupSet!.Id, prefix, ct), ct);
                    _subtreeCountCache[prefix] = count = fileCount;
                }
                total += count;
            }

            if (!ct.IsCancellationRequested)
                SelectedFileCount = total;
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection change; ignore.
        }
        finally
        {
            if (ReferenceEquals(_countCts, cts))
                _countCts = null;
            cts.Dispose();
        }
    }

    private bool CanRestore()
    {
        if (IsRestoring || !_hasSelection)
            return false;

        if (RestoreToOriginal)
            return true;

        return _selectedDriveKeys.All(key =>
            DriveDestinations.Any(d =>
                d.DriveKey.Equals(key, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(d.DestinationPath)));
    }

    /// <summary>Drive letter with colon (e.g. "D:"), or "_" for non-drive paths.</summary>
    private static string GetDriveLetter(string sourcePath) =>
        sourcePath.Length >= 2 && sourcePath[1] == ':'
            ? char.ToUpperInvariant(sourcePath[0]) + ":"
            : "_";

    /// <summary>Uppercase drive letter without colon (e.g. "D"), or "_".</summary>
    private static string GetDriveKey(string sourcePath) =>
        sourcePath.Length >= 2 && sourcePath[1] == ':'
            ? char.ToUpperInvariant(sourcePath[0]).ToString()
            : "_";

    private async Task RestoreAsync()
    {
        if (!CanRestore())
            return;

        _cts = new CancellationTokenSource();
        IsRestoring = true;
        IsComplete = false;
        StatusText = "Restoring files...";
        ResultDetail = "";

        try
        {
            // Materialise the selection into concrete restorable files, reading only
            // the selected subset of the catalog (never the whole set).
            var dirPrefixes = new List<string>();
            var filePaths = new List<string>();
            foreach (var root in Roots)
                root.CollectSelection(dirPrefixes, filePaths);

            CurrentFile = "Preparing file list…";
            var prepProgress = new Progress<int>(n =>
                CurrentFile = $"Preparing file list… {n:N0}");

            // Off the UI thread: materialisation issues catalog queries whose set-DB
            // lock completes synchronously when uncontended.
            var filesToRestore = await Task.Run(() => _restoreService.MaterializeSelectionAsync(
                _selectedBackupSet!.Id, dirPrefixes, filePaths, _cts.Token, prepProgress), _cts.Token);

            // Build the per-drive destination map: original roots when restoring
            // in place, otherwise the user's chosen folder for each drive.
            var mapping = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in DriveDestinations)
                mapping[d.DriveKey] = RestoreToOriginal ? d.OriginalRoot : d.DestinationPath;

            var progress = new Progress<RestoreProgress>(p =>
            {
                CurrentFile = p.CurrentFile;
                RestorePercentage = p.Percentage;
                ProgressText = $"{p.FilesCompleted}/{p.TotalFiles} files " +
                               $"({FormatBytes(p.BytesCompleted)}/{FormatBytes(p.TotalBytes)})";
            });

            var result = await _restoreService.RestoreAsync(
                filesToRestore, mapping, progress, _cts.Token);

            StatusText = result.Success
                ? "Restore completed successfully."
                : "Restore completed with errors.";

            ResultDetail = $"Files restored: {result.FilesRestored:N0}\n" +
                           $"Data restored: {FormatBytes(result.BytesRestored)}";

            if (result.FilesRestored > 0)
            {
                ResultDetail += "\n\nRestored to:";
                foreach (var d in DriveDestinations.Where(d => _selectedDriveKeys.Contains(d.DriveKey)))
                {
                    string dest = RestoreToOriginal ? d.OriginalRoot : d.DestinationPath;
                    ResultDetail += $"\n  {d.DriveLetter} → {dest}";
                }
            }

            if (result.Errors.Count > 0)
            {
                ResultDetail += $"\n\nErrors ({result.Errors.Count}):";
                foreach (var error in result.Errors.Take(10))
                    ResultDetail += $"\n  - {error}";
                if (result.Errors.Count > 10)
                    ResultDetail += $"\n  ... and {result.Errors.Count - 10} more";
            }
        }
        catch (OperationCanceledException)
        {
            StatusText = "Restore cancelled.";
        }
        catch (Exception ex)
        {
            StatusText = $"Restore failed: {ex.Message}";
        }
        finally
        {
            IsRestoring = false;
            IsComplete = true;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private static string FormatBytes(long bytes) => $"{bytes:N0}";
}
