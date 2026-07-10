using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;
using LithicBackup.Services;

namespace LithicBackup.ViewModels;

/// <summary>
/// ViewModel for the Verify Integrity view. Re-scans the backup set's sources
/// and uses <see cref="VerifyService"/> to confirm that every source file is
/// still represented in the backup, every catalog record's backing file exists
/// on disk, and every <c>.fileref</c> resolves to an existing plain copy of its
/// content.  Directory-mode backups only (optical sets are verified by inserting
/// discs through the restore flow).
/// </summary>
public class VerifyIntegrityViewModel : ViewModelBase
{
    private readonly ICatalogRepository _catalog;
    private readonly IFileScanner _scanner;
    private readonly BackupSet _backupSet;

    private bool _isLoading = true;
    private bool _isProgressIndeterminate = true;
    private double _progressPercent;
    private string _progressText = "Preparing verification...";
    private string _summaryText = "";
    private bool _succeeded;

    private int _sourceFilesChecked;
    private int _recordsChecked;
    private int _fileRefsChecked;
    private int _issueCount;
    private bool _verifyContents;
    private bool _contentsWereVerified;
    private int _itemsHashed;

    /// <summary>
    /// Distinct source paths whose backed-up copy is missing or broken in the
    /// destination (the backing file, .fileref target, dedup manifest, or a
    /// referenced block is gone). These are the records a "re-backup" repair
    /// marks deleted so the next backup copies them again.
    /// </summary>
    private readonly List<string> _repairablePaths = [];
    private int _repairableCount;
    private bool _isRepairing;
    private bool _repaired;

    private CancellationTokenSource? _cts;

    /// <summary>Fired when the user clicks "Close".</summary>
    public event Action? DoneRequested;

    public VerifyIntegrityViewModel(
        ICatalogRepository catalog,
        IFileScanner scanner,
        BackupSet backupSet)
    {
        _catalog = catalog;
        _scanner = scanner;
        _backupSet = backupSet;

        Issues = [];

        CloseCommand = new RelayCommand(_ =>
        {
            _cts?.Cancel();
            DoneRequested?.Invoke();
        });

        RepairCommand = new RelayCommand(async _ => await RepairAsync(), _ => CanRepair);
        VerifyCommand = new RelayCommand(async _ => await RunVerificationAsync(), _ => CanVerify);

        _ = RunVerificationAsync();
    }

    // --- Properties ---

    public string BackupSetName => _backupSet.Name;

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
                OnPropertyChanged(nameof(CanVerify));
        }
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    public double ProgressPercent
    {
        get => _progressPercent;
        private set => SetProperty(ref _progressPercent, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        private set => SetProperty(ref _summaryText, value);
    }

    /// <summary>True when verification finished with no issues.</summary>
    public bool Succeeded
    {
        get => _succeeded;
        private set => SetProperty(ref _succeeded, value);
    }

    public int SourceFilesChecked
    {
        get => _sourceFilesChecked;
        private set => SetProperty(ref _sourceFilesChecked, value);
    }

    public int RecordsChecked
    {
        get => _recordsChecked;
        private set => SetProperty(ref _recordsChecked, value);
    }

    public int FileRefsChecked
    {
        get => _fileRefsChecked;
        private set => SetProperty(ref _fileRefsChecked, value);
    }

    public int IssueCount
    {
        get => _issueCount;
        private set => SetProperty(ref _issueCount, value);
    }

    /// <summary>
    /// When set, the next verification reads every stored file/block back and
    /// re-hashes it (SHA-256), catching silent corruption / bit-rot. When clear
    /// (the default) verification is the fast existence-only check. Block-file
    /// presence is checked in either mode.
    /// </summary>
    public bool VerifyContents
    {
        get => _verifyContents;
        set => SetProperty(ref _verifyContents, value);
    }

    /// <summary>True when the most recent run actually read &amp; re-hashed content.</summary>
    public bool ContentsWereVerified
    {
        get => _contentsWereVerified;
        private set => SetProperty(ref _contentsWereVerified, value);
    }

    /// <summary>Number of files/blocks whose content was read back and re-hashed.</summary>
    public int ItemsHashed
    {
        get => _itemsHashed;
        private set => SetProperty(ref _itemsHashed, value);
    }

    /// <summary>True when a verification run can be started (not already running/repairing).</summary>
    public bool CanVerify => !_isLoading && !_isRepairing;

    public ObservableCollection<VerifyIssueItem> Issues { get; }

    public bool HasIssues => Issues.Count > 0;

    /// <summary>Number of distinct files whose backed-up copy is missing/broken.</summary>
    public int RepairableCount
    {
        get => _repairableCount;
        private set
        {
            if (SetProperty(ref _repairableCount, value))
            {
                OnPropertyChanged(nameof(HasRepairable));
                OnPropertyChanged(nameof(CanRepair));
            }
        }
    }

    /// <summary>True when there are missing/broken backed-up files that can be re-backed-up (and they haven't been already).</summary>
    public bool HasRepairable => _repairableCount > 0 && !_repaired;

    /// <summary>True while the catalog is being updated to flag files for re-backup.</summary>
    public bool IsRepairing
    {
        get => _isRepairing;
        private set
        {
            if (SetProperty(ref _isRepairing, value))
            {
                OnPropertyChanged(nameof(CanRepair));
                OnPropertyChanged(nameof(CanVerify));
            }
        }
    }

    /// <summary>True once the missing files have been flagged for re-backup.</summary>
    public bool Repaired
    {
        get => _repaired;
        private set
        {
            if (SetProperty(ref _repaired, value))
            {
                OnPropertyChanged(nameof(HasRepairable));
                OnPropertyChanged(nameof(CanRepair));
            }
        }
    }

    public bool CanRepair => HasRepairable && !_isRepairing;

    // --- Commands ---

    public ICommand CloseCommand { get; }

    public ICommand RepairCommand { get; }

    /// <summary>Starts (or restarts) verification using the current <see cref="VerifyContents"/> setting.</summary>
    public ICommand VerifyCommand { get; }

    // --- Logic ---

    /// <summary>
    /// Start a fresh verification run using the current <see cref="VerifyContents"/>
    /// setting. No-op if a run is already in progress.
    /// </summary>
    private async Task RunVerificationAsync()
    {
        if (IsLoading)
            return;

        // Fresh cancellation scope for this run. The previous run (if any) has
        // already completed, so disposing its source is safe.
        var cts = new CancellationTokenSource();
        var previous = _cts;
        _cts = cts;
        previous?.Dispose();

        await RunVerificationAsync(cts.Token);
    }

    private async Task RunVerificationAsync(CancellationToken ct)
    {
        // Reset UI state for a clean run (matters for re-verify).
        IsLoading = true;
        IsProgressIndeterminate = true;
        ProgressPercent = 0;
        ProgressText = "Preparing verification...";
        Issues.Clear();
        OnPropertyChanged(nameof(HasIssues));
        Succeeded = false;
        Repaired = false;
        RepairableCount = 0;
        bool runVerifyContents = _verifyContents;

        try
        {
            var opts = _backupSet.JobOptions;
            string? targetDir = opts?.TargetDirectory;
            if (string.IsNullOrWhiteSpace(targetDir))
            {
                SummaryText = "Integrity verification is available for directory-mode "
                    + "backups only. To verify an optical backup, use Restore and insert "
                    + "the discs.";
                IsLoading = false;
                return;
            }

            // Mirror the effective target dir resolution used by backups.
            string effectiveTargetDir = targetDir!;
            if (opts!.CreateSubdirectory && !string.IsNullOrWhiteSpace(opts.SubdirectoryName))
                effectiveTargetDir = Path.Combine(effectiveTargetDir, opts.SubdirectoryName.Trim());

            // Build the source selection tree (fall back to legacy SourceRoots).
            List<SourceSelection> sources;
            if (_backupSet.SourceSelections is { Count: > 0 } sel)
                sources = sel;
            else
                sources = _backupSet.SourceRoots
                    .Select(root => new SourceSelection
                    {
                        Path = root,
                        IsDirectory = true,
                        IsSelected = true,
                        AutoIncludeNewSubdirectories = true,
                    })
                    .ToList();

            var job = new BackupJob
            {
                BackupSetId = _backupSet.Id,
                Sources = sources,
                TargetDirectory = effectiveTargetDir,
                ExcludedExtensions = opts.ExcludedExtensions,
                TierSets = opts.TierSets,
            };

            var verifyService = new VerifyService(_catalog, _scanner);

            var progress = new Progress<VerifyProgress>(p =>
            {
                if (!string.IsNullOrEmpty(p.StatusMessage))
                    ProgressText = string.IsNullOrEmpty(p.CurrentFile)
                        ? p.StatusMessage!
                        : $"{p.StatusMessage} {p.CurrentFile}";
                if (p.TotalItems > 0)
                {
                    IsProgressIndeterminate = false;
                    ProgressPercent = p.Percentage;
                }
            });

            VerifyResult result = await Task.Run(
                () => verifyService.VerifyAsync(
                    job, effectiveTargetDir, runVerifyContents, progress, ct), ct);

            if (ct.IsCancellationRequested)
            {
                SummaryText = "Verification cancelled.";
                return;
            }

            SourceFilesChecked = result.SourceFilesChecked;
            RecordsChecked = result.RecordsChecked;
            FileRefsChecked = result.FileRefsChecked;
            IssueCount = result.Issues.Count;
            Succeeded = result.Success;
            ContentsWereVerified = result.ContentsVerified;
            ItemsHashed = result.ItemsHashed;

            const int maxDisplay = 5000;
            foreach (var issue in result.Issues.Take(maxDisplay))
            {
                Issues.Add(new VerifyIssueItem
                {
                    Path = issue.Path,
                    Detail = issue.Detail,
                    Kind = KindLabel(issue.Kind),
                });
            }
            OnPropertyChanged(nameof(HasIssues));

            // Collect the source paths whose backed-up copy is missing or broken
            // (catalog says "backed up" but the destination file/manifest/block is
            // gone). A normal incremental backup won't re-copy these because it
            // only compares the source to the catalog — so offer to flag them for
            // re-backup. (MissingFromBackup issues are the opposite — a source
            // file with no record at all — and are picked up by a normal backup
            // automatically, so they're not included here.)
            _repairablePaths.Clear();
            _repairablePaths.AddRange(result.Issues
                .Where(i => i.Kind is VerifyIssueKind.BackingFileMissing
                                   or VerifyIssueKind.FileRefUnresolved
                                   or VerifyIssueKind.BlockMissing
                                   or VerifyIssueKind.ContentMismatch)
                .Select(i => i.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase));
            RepairableCount = _repairablePaths.Count;

            string contentNote = result.ContentsVerified
                ? $" Content re-hashed for {result.ItemsHashed:N0} stored item(s)."
                : "";

            if (result.Success)
            {
                SummaryText = $"All good. {result.SourceFilesChecked:N0} source file(s) present, "
                    + $"{result.RecordsChecked:N0} backed-up file(s) verified"
                    + (result.FileRefsChecked > 0
                        ? $", {result.FileRefsChecked:N0} deduplicated reference(s) resolved."
                        : ".")
                    + contentNote;
            }
            else
            {
                SummaryText = $"Found {result.Issues.Count:N0} issue(s) across "
                    + $"{result.SourceFilesChecked:N0} source file(s) and "
                    + $"{result.RecordsChecked:N0} backed-up file(s)."
                    + contentNote
                    + (result.Issues.Count > maxDisplay
                        ? $" (showing first {maxDisplay:N0})"
                        : "");
            }
        }
        catch (OperationCanceledException)
        {
            SummaryText = "Verification cancelled.";
        }
        catch (Exception ex)
        {
            SummaryText = $"Verification failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// Flag every file whose backed-up copy is missing/broken (see
    /// <see cref="_repairablePaths"/>) as deleted in the catalog. Because the
    /// incremental diff ignores deleted records, the next backup sees those
    /// source files as no longer backed up and re-copies them to the destination.
    /// </summary>
    private async Task RepairAsync()
    {
        if (!CanRepair)
            return;

        IsRepairing = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            int marked = await _catalog.MarkFilesDeletedBySourcePathsAsync(
                _backupSet.Id, _repairablePaths);
            Repaired = true;
            SummaryText = $"Flagged {marked:N0} file(s) for re-backup. "
                + "Close this window and run Backup to copy them to the destination again.";
        }
        catch (Exception ex)
        {
            SummaryText = $"Could not update the catalog: {ex.Message}";
        }
        finally
        {
            IsRepairing = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private static string KindLabel(VerifyIssueKind kind) => kind switch
    {
        VerifyIssueKind.MissingFromBackup => "Not in backup",
        VerifyIssueKind.BackingFileMissing => "Missing file",
        VerifyIssueKind.FileRefUnresolved => "Broken reference",
        VerifyIssueKind.BlockMissing => "Missing block",
        VerifyIssueKind.ContentMismatch => "Corrupt content",
        _ => "Issue",
    };
}

/// <summary>A single integrity issue shown in the results list.</summary>
public class VerifyIssueItem
{
    public required string Path { get; init; }
    public required string Detail { get; init; }
    public required string Kind { get; init; }
}
