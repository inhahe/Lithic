using System.Collections.ObjectModel;
using System.Windows.Input;
using LithicBackup.Core;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.ViewModels;

/// <summary>
/// ViewModel for the Backup Coverage view. Scans the backup set's sources
/// and compares against the catalog to show what is/isn't backed up.
/// </summary>
public class BackupCoverageViewModel : ViewModelBase
{
    private readonly ICatalogRepository _catalog;
    private readonly IFileScanner _scanner;
    private readonly BackupSet _backupSet;

    private bool _isLoading = true;
    private bool _isProgressIndeterminate = true;
    private double _scanPercent;
    private string _scanProgressText = "Preparing scan...";
    private string _summaryText = "";

    private int _totalSourceFiles;
    private long _totalSourceBytes;
    private int _backedUpCount;
    private long _backedUpBytes;
    private int _notBackedUpCount;
    private long _notBackedUpBytes;
    private int _changedCount;
    private long _changedBytes;
    private double _coveragePercent;

    private CancellationTokenSource? _cts;

    /// <summary>Fired when the user clicks "Close".</summary>
    public event Action? DoneRequested;

    public BackupCoverageViewModel(
        ICatalogRepository catalog,
        IFileScanner scanner,
        BackupSet backupSet)
    {
        _catalog = catalog;
        _scanner = scanner;
        _backupSet = backupSet;

        NotBackedUpFiles = [];
        ChangedFiles = [];

        CloseCommand = new RelayCommand(_ =>
        {
            _cts?.Cancel();
            DoneRequested?.Invoke();
        });

        _cts = new CancellationTokenSource();
        _ = LoadAsync(_cts.Token);
    }

    // --- Properties ---

    public string BackupSetName => _backupSet.Name;

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    /// <summary>True when we have no estimate and the bar should animate.</summary>
    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    /// <summary>Estimated scan progress (0–100).</summary>
    public double ScanPercent
    {
        get => _scanPercent;
        private set => SetProperty(ref _scanPercent, value);
    }

    public string ScanProgressText
    {
        get => _scanProgressText;
        private set => SetProperty(ref _scanProgressText, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    public int TotalSourceFiles
    {
        get => _totalSourceFiles;
        private set => SetProperty(ref _totalSourceFiles, value);
    }

    public string TotalSourceSizeText => FormatBytes(_totalSourceBytes);

    public int BackedUpCount
    {
        get => _backedUpCount;
        private set => SetProperty(ref _backedUpCount, value);
    }

    public string BackedUpSizeText => FormatBytes(_backedUpBytes);

    public int NotBackedUpCount
    {
        get => _notBackedUpCount;
        private set => SetProperty(ref _notBackedUpCount, value);
    }

    public string NotBackedUpSizeText => FormatBytes(_notBackedUpBytes);

    public int ChangedCount
    {
        get => _changedCount;
        private set => SetProperty(ref _changedCount, value);
    }

    public string ChangedSizeText => FormatBytes(_changedBytes);

    public double CoveragePercent
    {
        get => _coveragePercent;
        private set => SetProperty(ref _coveragePercent, value);
    }

    public ObservableCollection<CoverageFileItem> NotBackedUpFiles { get; }
    public ObservableCollection<CoverageFileItem> ChangedFiles { get; }

    public bool HasNotBackedUp => NotBackedUpFiles.Count > 0;
    public bool HasChanged => ChangedFiles.Count > 0;

    // --- Commands ---

    public ICommand CloseCommand { get; }

    // --- Logic ---

    private async Task LoadAsync(CancellationToken ct)
    {
        try
        {
            // Build source selections (same pattern as StartIncrementalFlow).
            List<SourceSelection> sources;
            if (_backupSet.SourceSelections is { Count: > 0 })
            {
                sources = _backupSet.SourceSelections;
            }
            else
            {
                sources = _backupSet.SourceRoots
                    .Select(root => new SourceSelection
                    {
                        Path = root,
                        IsDirectory = true,
                        IsSelected = true,
                        AutoIncludeNewSubdirectories = true,
                    })
                    .ToList();
            }

            // Build exclusion filter.
            Func<string, bool>? isExcluded = null;
            if (_backupSet.JobOptions?.ExcludedExtensions is { Count: > 0 } patterns)
                isExcluded = GlobMatcher.CreateFilter(patterns);

            // Fast scalar count for the progress bar estimate, then load
            // full catalog data (needed later for diff classification).
            ScanProgressText = "Loading catalog data...";
            int estimatedTotal = 0;
            try { estimatedTotal = await _catalog.GetFileCountForBackupSetAsync(_backupSet.Id, ct); }
            catch { }
            var catalogInfo = await _catalog.GetLatestVersionInfoAsync(_backupSet.Id, ct);

            ct.ThrowIfCancellationRequested();

            // If no catalog estimate (new backup set), do a fast file count
            // so the progress bar is determinate instead of a looping animation.
            if (estimatedTotal == 0)
            {
                int[] counter = [0];

                var countTask = Task.Run(() =>
                {
                    foreach (var source in sources)
                    {
                        if (source.IsSelected == false || !source.IsDirectory)
                            continue;
                        if (!System.IO.Directory.Exists(source.Path))
                            continue;
                        CountFilesRecursive(
                            new System.IO.DirectoryInfo(source.Path), counter, ct);
                    }
                    return counter[0];
                }, ct);

                while (!countTask.IsCompleted)
                {
                    ScanProgressText = $"Counting files... {counter[0]:N0} found";
                    await Task.WhenAny(countTask, Task.Delay(1000, ct));
                }

                estimatedTotal = await countTask;
            }

            if (estimatedTotal > 0)
                IsProgressIndeterminate = false;

            // Scan source files on a background thread.
            var progress = new Progress<ScanProgress>(p =>
            {
                ScanProgressText = $"Scanning... {p.FilesFound:N0} files found ({FormatBytes(p.TotalBytes)})";
                if (estimatedTotal > 0)
                    ScanPercent = Math.Min(99, (double)p.FilesFound / estimatedTotal * 100);
            });

            ScanProgressText = "Scanning source directories...";
            var scannedFiles = await Task.Run(
                () => _scanner.ScanAsync(sources, progress, ct, isExcluded),
                ct);

            ct.ThrowIfCancellationRequested();

            // Compare.
            ScanPercent = 100;
            ScanProgressText = "Comparing...";
            long totalBytes = 0, backedUpBytes = 0, notBackedUpBytes = 0, changedBytes = 0;
            int backedUpCount = 0, notBackedUpCount = 0, changedCount = 0;
            var notBackedUp = new List<CoverageFileItem>();
            var changed = new List<CoverageFileItem>();

            foreach (var file in scannedFiles)
            {
                totalBytes += file.SizeBytes;

                if (catalogInfo.TryGetValue(file.FullPath, out var info))
                {
                    // File is in the catalog — check if it's changed since last backup.
                    if (file.SizeBytes != info.SizeBytes ||
                        file.LastWriteUtc > info.SourceLastWriteUtc)
                    {
                        changedCount++;
                        changedBytes += file.SizeBytes;
                        changed.Add(new CoverageFileItem
                        {
                            FilePath = file.FullPath,
                            SizeBytes = file.SizeBytes,
                        });
                    }
                    else
                    {
                        backedUpCount++;
                        backedUpBytes += file.SizeBytes;
                    }
                }
                else
                {
                    // File not in catalog — not backed up.
                    notBackedUpCount++;
                    notBackedUpBytes += file.SizeBytes;
                    notBackedUp.Add(new CoverageFileItem
                    {
                        FilePath = file.FullPath,
                        SizeBytes = file.SizeBytes,
                    });
                }
            }

            // Update properties (triggers UI refresh).
            TotalSourceFiles = scannedFiles.Count;
            _totalSourceBytes = totalBytes;
            OnPropertyChanged(nameof(TotalSourceSizeText));

            BackedUpCount = backedUpCount;
            _backedUpBytes = backedUpBytes;
            OnPropertyChanged(nameof(BackedUpSizeText));

            NotBackedUpCount = notBackedUpCount;
            _notBackedUpBytes = notBackedUpBytes;
            OnPropertyChanged(nameof(NotBackedUpSizeText));

            ChangedCount = changedCount;
            _changedBytes = changedBytes;
            OnPropertyChanged(nameof(ChangedSizeText));

            CoveragePercent = scannedFiles.Count > 0
                ? (double)backedUpCount / scannedFiles.Count * 100
                : 0;

            // Populate lists (cap at 2000 to avoid UI overload).
            const int maxDisplay = 2000;
            foreach (var item in notBackedUp.OrderByDescending(i => i.SizeBytes).Take(maxDisplay))
                NotBackedUpFiles.Add(item);
            foreach (var item in changed.OrderByDescending(i => i.SizeBytes).Take(maxDisplay))
                ChangedFiles.Add(item);

            OnPropertyChanged(nameof(HasNotBackedUp));
            OnPropertyChanged(nameof(HasChanged));

            SummaryText = notBackedUpCount == 0 && changedCount == 0
                ? "All source files are backed up and current."
                : $"{backedUpCount:N0} backed up, {notBackedUpCount:N0} not backed up, {changedCount:N0} changed since last backup.";

            if (notBackedUp.Count > maxDisplay)
                SummaryText += $" (showing largest {maxDisplay:N0} of {notBackedUp.Count:N0} not-backed-up files)";
        }
        catch (OperationCanceledException)
        {
            SummaryText = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            SummaryText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Recursively count files, writing to a shared counter that the UI
    /// thread polls on a timer.  Per-directory error handling so broken
    /// symlinks or inaccessible directories don't abort the entire count.
    /// </summary>
    private static void CountFilesRecursive(
        System.IO.DirectoryInfo dir, int[] counter, CancellationToken ct)
    {
        try
        {
            foreach (var _ in dir.EnumerateFiles())
            {
                counter[0]++;
                ct.ThrowIfCancellationRequested();
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            return;
        }

        try
        {
            foreach (var sub in dir.EnumerateDirectories())
            {
                ct.ThrowIfCancellationRequested();
                CountFilesRecursive(sub, counter, ct);
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested) { }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        double size = bytes;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return i == 0 ? $"{size:N0} {units[i]}" : $"{size:N1} {units[i]}";
    }
}

/// <summary>A single file entry in the coverage results.</summary>
public class CoverageFileItem
{
    public required string FilePath { get; init; }
    public required long SizeBytes { get; init; }
    public string SizeText => FormatBytes(SizeBytes);

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        double size = bytes;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return i == 0 ? $"{size:N0} {units[i]}" : $"{size:N1} {units[i]}";
    }
}
