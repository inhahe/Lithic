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
            // Build source selections and exclusion filter on a background
            // thread so deep selection trees don't stall the UI.
            var (sources, isExcluded) = await Task.Run(() =>
            {
                List<SourceSelection> src;
                if (_backupSet.SourceSelections is { Count: > 0 })
                    src = _backupSet.SourceSelections;
                else
                    src = _backupSet.SourceRoots
                        .Select(root => new SourceSelection
                        {
                            Path = root,
                            IsDirectory = true,
                            IsSelected = true,
                            AutoIncludeNewSubdirectories = true,
                        })
                        .ToList();

                Func<string, bool>? filter = null;
                if (_backupSet.JobOptions?.ExcludedExtensions is { Count: > 0 } patterns)
                    filter = GlobMatcher.CreateFilter(patterns);
                return (src, filter);
            });

            // Load catalog data on a background thread (can be slow for
            // large catalogs since the SQLite call is synchronous).
            ScanProgressText = "Loading catalog data...";
            var (estimatedTotal, catalogInfo) = await Task.Run(() =>
            {
                int count = 0;
                try { count = _catalog.GetFileCountForBackupSetAsync(_backupSet.Id, ct).GetAwaiter().GetResult(); }
                catch { }
                var info = _catalog.GetLatestVersionInfoAsync(_backupSet.Id, ct).GetAwaiter().GetResult();
                return (count, info);
            });

            if (ct.IsCancellationRequested) return;

            // When we have a catalog estimate, switch to a determinate bar.
            if (estimatedTotal > 0)
                IsProgressIndeterminate = false;

            // Scan source files on a background thread.
            var scanProgress = new LatestProgress<ScanProgress>();

            ScanProgressText = "Scanning source directories...";
            var scanTask = Task.Run(
                () => _scanner.ScanAsync(sources, scanProgress, ct, isExcluded));

            while (!scanTask.IsCompleted && !ct.IsCancellationRequested)
            {
                var p = scanProgress.Latest;
                if (p is not null)
                {
                    ScanProgressText = $"Scanning... {p.FilesFound:N0} files found ({FormatBytes(p.TotalBytes)})";
                    if (estimatedTotal > 0)
                        ScanPercent = Math.Min(99, (double)p.FilesFound / estimatedTotal * 100);
                }
                await Task.WhenAny(scanTask, Task.Delay(1000));
            }

            var scannedFiles = await scanTask;

            if (ct.IsCancellationRequested) return;

            // Compare on a background thread so large file sets don't
            // hang the UI.
            ScanPercent = 100;
            ScanProgressText = "Comparing...";

            var cmp = await Task.Run(() =>
            {
                long totalB = 0, backedUpB = 0, notBackedUpB = 0, changedB = 0;
                int backedUpC = 0, notBackedUpC = 0, changedC = 0;
                var nbList = new List<CoverageFileItem>();
                var chList = new List<CoverageFileItem>();

                foreach (var file in scannedFiles)
                {
                    totalB += file.SizeBytes;

                    if (catalogInfo.TryGetValue(file.FullPath, out var info))
                    {
                        if (file.SizeBytes != info.SizeBytes ||
                            file.LastWriteUtc > info.SourceLastWriteUtc)
                        {
                            changedC++;
                            changedB += file.SizeBytes;
                            chList.Add(new CoverageFileItem
                            {
                                FilePath = file.FullPath,
                                SizeBytes = file.SizeBytes,
                            });
                        }
                        else
                        {
                            backedUpC++;
                            backedUpB += file.SizeBytes;
                        }
                    }
                    else
                    {
                        notBackedUpC++;
                        notBackedUpB += file.SizeBytes;
                        nbList.Add(new CoverageFileItem
                        {
                            FilePath = file.FullPath,
                            SizeBytes = file.SizeBytes,
                        });
                    }
                }

                return (totalB, backedUpB, backedUpC, notBackedUpB, notBackedUpC,
                        changedB, changedC, nbList, chList);
            });

            // Update properties (triggers UI refresh).
            TotalSourceFiles = scannedFiles.Count;
            _totalSourceBytes = cmp.totalB;
            OnPropertyChanged(nameof(TotalSourceSizeText));

            BackedUpCount = cmp.backedUpC;
            _backedUpBytes = cmp.backedUpB;
            OnPropertyChanged(nameof(BackedUpSizeText));

            NotBackedUpCount = cmp.notBackedUpC;
            _notBackedUpBytes = cmp.notBackedUpB;
            OnPropertyChanged(nameof(NotBackedUpSizeText));

            ChangedCount = cmp.changedC;
            _changedBytes = cmp.changedB;
            OnPropertyChanged(nameof(ChangedSizeText));

            CoveragePercent = scannedFiles.Count > 0
                ? (double)cmp.backedUpC / scannedFiles.Count * 100
                : 0;

            // Populate lists (cap at 2000 to avoid UI overload).
            const int maxDisplay = 2000;
            foreach (var item in cmp.nbList.OrderByDescending(i => i.SizeBytes).Take(maxDisplay))
                NotBackedUpFiles.Add(item);
            foreach (var item in cmp.chList.OrderByDescending(i => i.SizeBytes).Take(maxDisplay))
                ChangedFiles.Add(item);

            OnPropertyChanged(nameof(HasNotBackedUp));
            OnPropertyChanged(nameof(HasChanged));

            SummaryText = cmp.notBackedUpC == 0 && cmp.changedC == 0
                ? "All source files are backed up and current."
                : $"{cmp.backedUpC:N0} backed up, {cmp.notBackedUpC:N0} not backed up, {cmp.changedC:N0} changed since last backup.";

            if (cmp.nbList.Count > maxDisplay)
                SummaryText += $" (showing largest {maxDisplay:N0} of {cmp.nbList.Count:N0} not-backed-up files)";
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
