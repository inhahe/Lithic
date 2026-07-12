using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.ViewModels;

/// <summary>
/// ViewModel for the Test Disc view. Integrity-tests one already-burned optical
/// disc against the catalog (existence + size, plus optional SHA-256 content
/// re-hash) using <see cref="IRestoreService.VerifyDiscAsync"/>. When the disc
/// fails, offers two repairs:
/// <list type="bullet">
///   <item><b>Re-burn whole disc</b> — re-stages every file the disc held from
///   the live source and burns a fresh replacement disc
///   (<see cref="IBackupOrchestrator.ReplaceDiscAsync(int, string, IProgress{BackupProgress}?, CancellationToken)"/>).</item>
///   <item><b>Re-burn affected files</b> — re-burns only the files that failed
///   the test onto a new supplementary disc, superseding the bad records
///   (<see cref="IBackupOrchestrator.ReplaceDiscFilesAsync"/>).</item>
/// </list>
/// Optical (disc-based) backup sets only.
/// </summary>
public class TestDiscViewModel : ViewModelBase
{
    private readonly ICatalogRepository _catalog;
    private readonly IRestoreService _restoreService;
    private readonly IBackupOrchestrator _orchestrator;
    private readonly IDiscBurner _burner;
    private readonly BackupSet _backupSet;

    private bool _isLoadingDiscs = true;
    private DiscOption? _selectedDisc;
    private OpticalDriveOption? _selectedDrive;

    private bool _verifyContents = true;
    private bool _isTesting;
    private bool _isRemediating;
    private bool _testCompleted;
    private bool _succeeded;
    private bool _contentsWereVerified;
    private bool _remediated;

    private bool _isProgressIndeterminate = true;
    private double _progressPercent;
    private string _progressText = "";
    private string _summaryText = "";

    private int _filesChecked;
    private long _bytesChecked;
    private int _issueCount;

    private IReadOnlyList<long> _failedFileRecordIds = [];
    private CancellationTokenSource? _cts;

    /// <summary>Fired when the user clicks "Close".</summary>
    public event Action? DoneRequested;

    public TestDiscViewModel(
        ICatalogRepository catalog,
        IRestoreService restoreService,
        IBackupOrchestrator orchestrator,
        IDiscBurner burner,
        BackupSet backupSet)
    {
        _catalog = catalog;
        _restoreService = restoreService;
        _orchestrator = orchestrator;
        _burner = burner;
        _backupSet = backupSet;

        Discs = [];
        Drives = [];
        Issues = [];

        CloseCommand = new RelayCommand(_ =>
        {
            _cts?.Cancel();
            DoneRequested?.Invoke();
        });
        TestCommand = new RelayCommand(async _ => await RunTestAsync(), _ => CanTest);
        RefreshDrivesCommand = new RelayCommand(_ => RefreshDrives());
        ReplaceWholeDiscCommand = new RelayCommand(
            async _ => await RemediateAsync(affectedOnly: false), _ => CanReplaceWhole);
        ReplaceAffectedFilesCommand = new RelayCommand(
            async _ => await RemediateAsync(affectedOnly: true), _ => CanReplaceAffected);

        _ = LoadDiscsAsync();
    }

    // --- Properties ---

    public string BackupSetName => _backupSet.Name;

    public ObservableCollection<DiscOption> Discs { get; }
    public ObservableCollection<OpticalDriveOption> Drives { get; }
    public ObservableCollection<TestDiscIssueItem> Issues { get; }

    public bool IsLoadingDiscs
    {
        get => _isLoadingDiscs;
        private set
        {
            if (SetProperty(ref _isLoadingDiscs, value))
                RaiseCanChanged();
        }
    }

    public DiscOption? SelectedDisc
    {
        get => _selectedDisc;
        set
        {
            if (SetProperty(ref _selectedDisc, value))
            {
                // A fresh disc selection resets any prior test results and
                // re-picks the mounted drive that matches this disc's label.
                ResetResults();
                AutoSelectDrive();
                RaiseCanChanged();
            }
        }
    }

    public OpticalDriveOption? SelectedDrive
    {
        get => _selectedDrive;
        set
        {
            if (SetProperty(ref _selectedDrive, value))
                RaiseCanChanged();
        }
    }

    /// <summary>
    /// When set (the default), the test reads every stored file/block back and
    /// re-hashes it (SHA-256), catching silent corruption / bit-rot — which
    /// typically strikes in the middle of a file's data, invisible to an
    /// existence + size check. Clear it for the fast existence + size only test.
    /// </summary>
    public bool VerifyContents
    {
        get => _verifyContents;
        set => SetProperty(ref _verifyContents, value);
    }

    /// <summary>True while a disc test is running.</summary>
    public bool IsTesting
    {
        get => _isTesting;
        private set
        {
            if (SetProperty(ref _isTesting, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(IsIdle));
                RaiseCanChanged();
            }
        }
    }

    /// <summary>True while a re-burn repair is running.</summary>
    public bool IsRemediating
    {
        get => _isRemediating;
        private set
        {
            if (SetProperty(ref _isRemediating, value))
            {
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(IsIdle));
                RaiseCanChanged();
            }
        }
    }

    /// <summary>True while either a test or a repair is running.</summary>
    public bool IsBusy => _isTesting || _isRemediating;

    /// <summary>True when no test/repair is running (used to enable the pickers).</summary>
    public bool IsIdle => !IsBusy;

    /// <summary>True once a test has finished (successfully or with issues).</summary>
    public bool TestCompleted
    {
        get => _testCompleted;
        private set
        {
            if (SetProperty(ref _testCompleted, value))
            {
                OnPropertyChanged(nameof(HasFailure));
                RaiseCanChanged();
            }
        }
    }

    /// <summary>True when the most recent test found no issues.</summary>
    public bool Succeeded
    {
        get => _succeeded;
        private set
        {
            if (SetProperty(ref _succeeded, value))
            {
                OnPropertyChanged(nameof(HasFailure));
                RaiseCanChanged();
            }
        }
    }

    /// <summary>True when the last test actually read &amp; re-hashed content.</summary>
    public bool ContentsWereVerified
    {
        get => _contentsWereVerified;
        private set => SetProperty(ref _contentsWereVerified, value);
    }

    /// <summary>True once a repair (re-burn) has completed for this disc.</summary>
    public bool Remediated
    {
        get => _remediated;
        private set
        {
            if (SetProperty(ref _remediated, value))
            {
                OnPropertyChanged(nameof(HasFailure));
                RaiseCanChanged();
            }
        }
    }

    /// <summary>True when the test found issues and no repair has been applied yet.</summary>
    public bool HasFailure => _testCompleted && !_succeeded && !_remediated;

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

    public int FilesChecked
    {
        get => _filesChecked;
        private set => SetProperty(ref _filesChecked, value);
    }

    public long BytesChecked
    {
        get => _bytesChecked;
        private set => SetProperty(ref _bytesChecked, value);
    }

    public int IssueCount
    {
        get => _issueCount;
        private set => SetProperty(ref _issueCount, value);
    }

    public bool HasIssues => Issues.Count > 0;

    /// <summary>Number of failed files that can be re-burned onto a supplementary disc.</summary>
    public int AffectedFileCount => _failedFileRecordIds.Count;

    // --- Command gating ---

    public bool CanTest =>
        !_isLoadingDiscs && !IsBusy && _selectedDisc is not null && _selectedDrive is not null;

    public bool CanReplaceWhole => HasFailure && !IsBusy && _selectedDisc is not null;

    public bool CanReplaceAffected =>
        HasFailure && !IsBusy && _selectedDisc is not null && _failedFileRecordIds.Count > 0;

    // --- Commands ---

    public ICommand CloseCommand { get; }
    public ICommand TestCommand { get; }
    public ICommand RefreshDrivesCommand { get; }
    public ICommand ReplaceWholeDiscCommand { get; }
    public ICommand ReplaceAffectedFilesCommand { get; }

    // --- Logic ---

    private async Task LoadDiscsAsync()
    {
        try
        {
            var discs = await _catalog.GetDiscsForBackupSetAsync(_backupSet.Id);
            Discs.Clear();
            foreach (var d in discs.OrderBy(d => d.SequenceNumber))
                Discs.Add(new DiscOption(d));

            RefreshDrives();

            if (Discs.Count == 0)
                SummaryText = "This backup set has no discs recorded in the catalog.";
            else
                SelectedDisc = Discs[0];
        }
        catch (Exception ex)
        {
            SummaryText = $"Could not load discs: {ex.Message}";
        }
        finally
        {
            IsLoadingDiscs = false;
        }
    }

    /// <summary>Re-enumerate ready optical drives and re-run auto-selection.</summary>
    private void RefreshDrives()
    {
        var previousRoot = _selectedDrive?.Root;
        Drives.Clear();
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.CDRom || !drive.IsReady)
                continue;
            string label;
            try { label = drive.VolumeLabel; }
            catch { label = ""; }
            Drives.Add(new OpticalDriveOption(drive.RootDirectory.FullName, label));
        }

        // Preserve the prior pick if still present, else auto-detect by label.
        if (previousRoot is not null)
            _selectedDrive = Drives.FirstOrDefault(d => d.Root == previousRoot);
        OnPropertyChanged(nameof(SelectedDrive));
        AutoSelectDrive();
        RaiseCanChanged();
    }

    /// <summary>Pick the mounted drive whose volume label matches the selected disc.</summary>
    private void AutoSelectDrive()
    {
        if (_selectedDrive is not null || _selectedDisc is null)
            return;

        var match = Drives.FirstOrDefault(d =>
            string.Equals(d.Label, _selectedDisc.Disc.Label, StringComparison.OrdinalIgnoreCase));
        if (match is not null)
        {
            _selectedDrive = match;
            OnPropertyChanged(nameof(SelectedDrive));
        }
        else if (Drives.Count == 1)
        {
            // Only one optical drive — assume it's the one to test.
            _selectedDrive = Drives[0];
            OnPropertyChanged(nameof(SelectedDrive));
        }
    }

    private async Task RunTestAsync()
    {
        if (!CanTest || _selectedDisc is null || _selectedDrive is null)
            return;

        var disc = _selectedDisc.Disc;
        var drive = _selectedDrive;

        // Guard: the mounted disc's label should match the one we're testing.
        // Warn but let the user proceed (labels can be edited/blank).
        string labelWarning = "";
        if (!string.Equals(drive.Label, disc.Label, StringComparison.OrdinalIgnoreCase))
            labelWarning = $" (warning: inserted disc is labelled \"{drive.Label}\", "
                + $"expected \"{disc.Label}\")";

        ResetResults();
        IsTesting = true;
        IsProgressIndeterminate = true;
        ProgressPercent = 0;
        ProgressText = $"Testing disc \"{disc.Label}\"...";
        bool runVerifyContents = _verifyContents;

        var cts = new CancellationTokenSource();
        var previous = _cts;
        _cts = cts;
        previous?.Dispose();

        try
        {
            var progress = new Progress<RestoreProgress>(p =>
            {
                if (p.TotalFiles > 0)
                {
                    IsProgressIndeterminate = false;
                    ProgressPercent = p.Percentage;
                    ProgressText = string.IsNullOrEmpty(p.CurrentFile)
                        ? $"Checking {p.FilesCompleted:N0} / {p.TotalFiles:N0}"
                        : $"Checking ({p.FilesCompleted:N0}/{p.TotalFiles:N0}): {p.CurrentFile}";
                }
            });

            DiscVerifyResult result = await Task.Run(
                () => _restoreService.VerifyDiscAsync(
                    disc.Id, drive.Root, runVerifyContents, progress, cts.Token), cts.Token);

            if (result.Cancelled || cts.IsCancellationRequested)
            {
                SummaryText = "Disc test cancelled.";
                return;
            }

            FilesChecked = result.FilesChecked;
            BytesChecked = result.BytesChecked;
            IssueCount = result.Issues.Count;
            ContentsWereVerified = result.ContentsVerified;
            _failedFileRecordIds = result.FailedFileRecordIds;
            OnPropertyChanged(nameof(AffectedFileCount));

            const int maxDisplay = 5000;
            foreach (var issue in result.Issues.Take(maxDisplay))
            {
                Issues.Add(new TestDiscIssueItem
                {
                    Path = issue.SourcePath,
                    Detail = issue.Detail,
                    Kind = KindLabel(issue.Kind),
                });
            }
            OnPropertyChanged(nameof(HasIssues));

            Succeeded = result.Success;
            TestCompleted = true;

            string contentNote = result.ContentsVerified
                ? " Stored data was read back and re-hashed."
                : " (existence + size check; contents not re-hashed)";

            if (result.Success)
            {
                SummaryText = $"Disc passed. {result.FilesChecked:N0} file(s) verified"
                    + $" ({FormatBytes(result.BytesChecked)})." + contentNote + labelWarning;
            }
            else
            {
                int unresolved = result.Issues.Count - result.FailedFileRecordIds.Count;
                string unresolvedNote = unresolved > 0
                    ? $" {unresolved:N0} cross-disc reference(s) could not be checked from this disc alone."
                    : "";
                SummaryText = $"Disc FAILED: {result.Issues.Count:N0} problem(s) across "
                    + $"{result.FilesChecked:N0} file(s) checked."
                    + contentNote + unresolvedNote + labelWarning
                    + (result.Issues.Count > maxDisplay ? $" (showing first {maxDisplay:N0})" : "");
            }
        }
        catch (OperationCanceledException)
        {
            SummaryText = "Disc test cancelled.";
        }
        catch (Exception ex)
        {
            SummaryText = $"Disc test failed: {ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>
    /// Re-burn to repair the failed disc. When <paramref name="affectedOnly"/>
    /// is true, only the files that failed the test are burned onto a new
    /// supplementary disc; otherwise every file the disc held is re-staged and
    /// burned onto a fresh replacement disc.
    /// </summary>
    private async Task RemediateAsync(bool affectedOnly)
    {
        if (IsBusy || _selectedDisc is null)
            return;
        if (affectedOnly && _failedFileRecordIds.Count == 0)
            return;

        var recorderId = TryGetRecorderId();
        if (recorderId is null)
        {
            SummaryText = "No optical recorder was found to burn a new disc.";
            return;
        }

        int discId = _selectedDisc.Disc.Id;

        IsRemediating = true;
        IsProgressIndeterminate = true;
        ProgressPercent = 0;
        ProgressText = affectedOnly
            ? "Re-burning affected files to a new disc..."
            : "Re-burning the whole disc...";

        var cts = new CancellationTokenSource();
        var previous = _cts;
        _cts = cts;
        previous?.Dispose();

        try
        {
            var progress = new Progress<BackupProgress>(p =>
            {
                if (!string.IsNullOrEmpty(p.StatusMessage))
                    ProgressText = p.StatusMessage!;
                else if (!string.IsNullOrEmpty(p.CurrentFile))
                    ProgressText = p.CurrentFile;
                if (p.OverallPercentage > 0)
                {
                    IsProgressIndeterminate = false;
                    ProgressPercent = p.OverallPercentage;
                }
            });

            if (affectedOnly)
            {
                int burned = await Task.Run(
                    () => _orchestrator.ReplaceDiscFilesAsync(
                        discId, _failedFileRecordIds, recorderId, progress, cts.Token), cts.Token);
                Remediated = true;
                SummaryText = burned > 0
                    ? $"Re-burned {burned:N0} affected file(s) onto a new supplementary disc. "
                        + "Label and store it with the set; the old records now resolve to the fresh copies."
                    : "No files could be re-burned — their live sources no longer exist on disk. "
                        + "Restore them from another good disc instead.";
            }
            else
            {
                await Task.Run(
                    () => _orchestrator.ReplaceDiscAsync(
                        discId, recorderId, progress, cts.Token), cts.Token);
                Remediated = true;
                SummaryText = "Burned a fresh replacement disc from the live sources. "
                    + "Label it and discard the old disc.";
            }
        }
        catch (OperationCanceledException)
        {
            SummaryText = "Re-burn cancelled.";
        }
        catch (Exception ex)
        {
            SummaryText = $"Re-burn failed: {ex.Message}";
        }
        finally
        {
            IsRemediating = false;
        }
    }

    private string? TryGetRecorderId()
    {
        try
        {
            var ids = _burner.GetRecorderIds();
            return ids.Count > 0 ? ids[0] : null;
        }
        catch
        {
            return null;
        }
    }

    private void ResetResults()
    {
        Issues.Clear();
        OnPropertyChanged(nameof(HasIssues));
        TestCompleted = false;
        Succeeded = false;
        Remediated = false;
        ContentsWereVerified = false;
        FilesChecked = 0;
        BytesChecked = 0;
        IssueCount = 0;
        _failedFileRecordIds = [];
        OnPropertyChanged(nameof(AffectedFileCount));
        SummaryText = "";
        ProgressPercent = 0;
        ProgressText = "";
    }

    private void RaiseCanChanged()
    {
        OnPropertyChanged(nameof(CanTest));
        OnPropertyChanged(nameof(CanReplaceWhole));
        OnPropertyChanged(nameof(CanReplaceAffected));
        CommandManager.InvalidateRequerySuggested();
    }

    private static string KindLabel(DiscFileIssueKind kind) => kind switch
    {
        DiscFileIssueKind.Missing => "Missing on disc",
        DiscFileIssueKind.SizeMismatch => "Wrong size",
        DiscFileIssueKind.ContentMismatch => "Corrupt content",
        DiscFileIssueKind.Unreadable => "Unreadable",
        DiscFileIssueKind.UnresolvedReference => "Cross-disc reference",
        _ => "Issue",
    };

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int u = 0;
        while (size >= 1024 && u < units.Length - 1) { size /= 1024; u++; }
        return $"{size:0.##} {units[u]}";
    }
}

/// <summary>A disc from the backup set, shown in the disc picker.</summary>
public class DiscOption
{
    public DiscOption(DiscRecord disc) => Disc = disc;

    public DiscRecord Disc { get; }

    public string Display =>
        $"#{Disc.SequenceNumber} · {Disc.Label}"
        + (Disc.IsBad ? " (marked bad)" : "");
}

/// <summary>A mounted optical drive, shown in the drive picker.</summary>
public class OpticalDriveOption
{
    public OpticalDriveOption(string root, string label)
    {
        Root = root;
        Label = label ?? "";
    }

    /// <summary>Mounted volume root, e.g. <c>"E:\"</c>.</summary>
    public string Root { get; }

    /// <summary>Volume label of the inserted disc.</summary>
    public string Label { get; }

    public string Display =>
        string.IsNullOrWhiteSpace(Label)
            ? $"{Root.TrimEnd('\\')} (no label)"
            : $"{Root.TrimEnd('\\')} — {Label}";
}

/// <summary>A single problem found during a disc test, shown in the results list.</summary>
public class TestDiscIssueItem
{
    public required string Path { get; init; }
    public required string Detail { get; init; }
    public required string Kind { get; init; }
}
