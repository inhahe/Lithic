using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.ViewModels;

/// <summary>
/// ViewModel for the file restore view. Shows backup sets, their files,
/// and allows the user to restore selected files.
/// </summary>
public class RestoreViewModel : ViewModelBase
{
    private readonly ICatalogRepository _catalog;
    private readonly IRestoreService _restoreService;

    private BackupSet? _selectedBackupSet;
    private bool _restoreToOriginal;
    private HashSet<string> _selectedDriveKeys = new(StringComparer.OrdinalIgnoreCase);
    private bool _isLoading;
    private bool _isRestoring;
    private bool _isComplete;
    private string _statusText = "Select a backup set to see available files.";
    private string _resultDetail = "";
    private double _restorePercentage;
    private string _currentFile = "";
    private string _progressText = "";
    private CancellationTokenSource? _cts;

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

        // Load files immediately for the given backup set.
        _ = LoadRestorableFilesAsync(backupSet.Id);
    }

    // --- Properties ---

    public BackupSet SelectedBackupSet => _selectedBackupSet!;

    /// <summary>Root nodes of the restorable-file tree (one per drive).</summary>
    public ObservableCollection<RestoreNodeViewModel> Roots { get; }

    /// <summary>Number of files currently checked for restore.</summary>
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
            // A click forces a definite state (false when toggling off an
            // indeterminate box); apply it to every root, which cascades down.
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

    private async Task LoadRestorableFilesAsync(int backupSetId)
    {
        IsLoading = true;
        StatusText = "Loading files from catalog...";
        Roots.Clear();
        SelectedFileCount = 0;

        try
        {
            var files = await _restoreService.GetRestorableFilesAsync(backupSetId);

            // Group by source path, show latest version.
            var latestByPath = files
                .GroupBy(f => f.Record.SourcePath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderByDescending(f => f.Record.BackedUpUtc).First())
                .OrderBy(f => f.Record.SourcePath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            BuildTree(latestByPath);

            // One destination row per distinct source drive.
            DriveDestinations.Clear();
            var drives = latestByPath
                .Select(f => GetDriveLetter(f.Record.SourcePath))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(d => d, StringComparer.OrdinalIgnoreCase);
            foreach (var drive in drives)
            {
                var driveVm = new DriveDestinationViewModel(drive);
                // Re-evaluate the Restore button when a destination is edited.
                driveVm.PropertyChanged += (_, _) =>
                    CommandManager.InvalidateRequerySuggested();
                DriveDestinations.Add(driveVm);
            }

            OnPropertyChanged(nameof(IsAllSelected));
            StatusText = $"{latestByPath.Count:N0} files available for restore.";
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to load files: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Build the directory/file tree from the flat list of restorable files.
    /// Each file's full source path (e.g. <c>D:\dir\sub\file.txt</c>) is split
    /// into path segments; directory nodes are created/reused on the way down
    /// and the file is attached as a leaf. Directories sort before files, both
    /// alphabetically. Directory sizes aggregate their descendants.
    /// </summary>
    private void BuildTree(IReadOnlyList<RestorableFile> files)
    {
        // Map of directory full-path -> node, so repeated prefixes are reused.
        var dirNodes = new Dictionary<string, RestoreNodeViewModel>(StringComparer.OrdinalIgnoreCase);
        var rootNodes = new Dictionary<string, RestoreNodeViewModel>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            string path = file.Record.SourcePath;
            string[] segments = path.Split(
                ['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                continue;

            // Walk/create the directory chain for everything but the last
            // segment (which is the file name).
            RestoreNodeViewModel? parent = null;
            string accumulated = "";
            for (int i = 0; i < segments.Length - 1; i++)
            {
                accumulated = accumulated.Length == 0
                    ? segments[i] + (segments[i].EndsWith(':') ? "\\" : "")
                    : Path.Combine(accumulated, segments[i]);

                if (!dirNodes.TryGetValue(accumulated, out var dirNode))
                {
                    dirNode = new RestoreNodeViewModel(
                        segments[i], accumulated, i, OnNodeSelectionChanged);
                    dirNodes[accumulated] = dirNode;

                    if (parent is null)
                        rootNodes[accumulated] = dirNode;
                    else
                    {
                        dirNode.Parent = parent;
                        parent.Children.Add(dirNode);
                    }
                }
                parent = dirNode;
            }

            // Leaf file node.
            string fileName = segments[^1];
            var fileNode = new RestoreNodeViewModel(
                fileName, file, segments.Length - 1, OnNodeSelectionChanged)
            {
                Parent = parent,
            };
            if (parent is null)
                rootNodes[path] = fileNode;
            else
                parent.Children.Add(fileNode);
        }

        // Sort each node's children (directories first, then files), aggregate
        // directory sizes, and publish the roots.
        foreach (var root in rootNodes.Values
                     .OrderByDescending(n => n.IsDirectory)
                     .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase))
        {
            SortAndAggregate(root);
            // Expand the top level so the tree isn't a single collapsed row.
            if (root.IsDirectory)
                root.IsExpanded = true;
            Roots.Add(root);
        }
    }

    /// <summary>
    /// Recursively sort a node's children (directories before files, each
    /// alphabetical) and roll descendant file sizes up into directory sizes.
    /// </summary>
    private static long SortAndAggregate(RestoreNodeViewModel node)
    {
        if (!node.IsDirectory)
            return node.SizeBytes;

        var sorted = node.Children
            .OrderByDescending(c => c.IsDirectory)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        node.Children.Clear();

        long total = 0;
        foreach (var child in sorted)
        {
            total += SortAndAggregate(child);
            node.Children.Add(child);
        }
        node.SizeBytes = total;
        return total;
    }

    private void OnNodeSelectionChanged()
    {
        var selected = new List<RestorableFile>();
        foreach (var root in Roots)
            root.CollectSelectedFiles(selected);
        SelectedFileCount = selected.Count;
        _selectedDriveKeys = selected
            .Select(f => GetDriveKey(f.Record.SourcePath))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        OnPropertyChanged(nameof(IsAllSelected));
        CommandManager.InvalidateRequerySuggested();
    }

    private bool CanRestore()
    {
        if (IsRestoring || SelectedFileCount == 0)
            return false;

        // Restoring to original locations needs no destinations; otherwise
        // every drive that has selected files must have a destination set.
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
            var filesToRestore = new List<RestorableFile>();
            foreach (var root in Roots)
                root.CollectSelectedFiles(filesToRestore);

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

            // Show where files landed so they're easy to find.
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
