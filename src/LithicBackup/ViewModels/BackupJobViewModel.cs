using System.Collections.ObjectModel;
using System.Windows.Input;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;
using LithicBackup.Services;

namespace LithicBackup.ViewModels;

/// <summary>
/// ViewModel for configuring and launching a backup job.
/// Shows editable job options and (after planning) a summary of what will be burned.
/// </summary>
public class BackupJobViewModel : ViewModelBase
{
    private string _setName = $"Backup {DateTime.Now:yyyy-MM-dd}";
    private ZipMode _zipMode = ZipMode.IncompatibleOnly;
    private FilesystemType _filesystemType = FilesystemType.UDF;
    private string _capacityOverrideGb = "";
    private bool _verifyAfterBurn = true;
    private bool _includeCatalogOnDisc = true;
    private bool _allowFileSplitting = true;

    private bool _enableFileDeduplication;
    private bool _enableDeduplication;
    private string _deduplicationBlockSizeKb = "64";

    private bool _isDirectoryMode;
    private string _targetDirectoryPath = "";
    private bool _createSubdirectory;
    private string _subdirectoryName = "";
    private string _excludedExtensions = "";

    private bool _scheduleEnabled;
    private ScheduleMode _scheduleMode = ScheduleMode.Interval;
    private string _scheduleIntervalHours = "24";
    private int _scheduleDailyHour = 2;
    private int _scheduleDailyMinute;
    private string _scheduleDebounceSeconds = "60";

    private bool _isPlanning;
    private bool _isPlanReady;
    private string _planSummary = "";
    private string _mediaInfoText = "";

    private BackupPlan? _plan;

    /// <summary>Fired when the user clicks "Start Backup" after a plan is ready.</summary>
    public event Action<BackupPlan>? StartRequested;

    /// <summary>Fired when the user clicks "Back".</summary>
    public event Action? BackRequested;

    /// <summary>Fired after planning succeeds, passing the constructed BackupJob for persistence.</summary>
    public event Action<BackupJob>? PlanCompleted;

    public BackupJobViewModel(
        List<SourceSelection> sources,
        IBackupOrchestrator orchestrator,
        IDiscBurner burner,
        DirectoryBackupService? directoryBackupService = null)
    {
        Sources = sources;
        Orchestrator = orchestrator;
        Burner = burner;
        DirectoryBackup = directoryBackupService;

        PlanCommand = new RelayCommand(_ => _ = PlanAsync(), _ => !IsPlanning);
        StartCommand = new RelayCommand(_ => OnStartRequested(), _ => IsPlanReady);
        BackCommand = new RelayCommand(_ => BackRequested?.Invoke());
        BrowseTargetDirectoryCommand = new RelayCommand(_ => BrowseTargetDirectory());
        AddTierCommand = new RelayCommand(_ => AddRetentionTier());

        // Initialize retention tiers from defaults.
        RetentionTiers = [];
        TierSets = [];
        foreach (var tier in VersionRetentionService.DefaultTiers)
        {
            var vm = RetentionTierViewModel.FromModel(tier);
            vm.RemoveRequested += t => RetentionTiers.Remove(t);
            RetentionTiers.Add(vm);
        }

        // Show media info on load.
        _ = LoadMediaInfoAsync();
    }

    internal List<SourceSelection> Sources { get; }
    internal IBackupOrchestrator Orchestrator { get; }
    internal IDiscBurner Burner { get; }
    internal DirectoryBackupService? DirectoryBackup { get; }

    // --- Editable options ---

    public string SetName
    {
        get => _setName;
        set => SetProperty(ref _setName, value);
    }

    public ZipMode ZipMode
    {
        get => _zipMode;
        set => SetProperty(ref _zipMode, value);
    }

    public bool VerifyAfterBurn
    {
        get => _verifyAfterBurn;
        set => SetProperty(ref _verifyAfterBurn, value);
    }

    public bool IncludeCatalogOnDisc
    {
        get => _includeCatalogOnDisc;
        set => SetProperty(ref _includeCatalogOnDisc, value);
    }

    public bool AllowFileSplitting
    {
        get => _allowFileSplitting;
        set => SetProperty(ref _allowFileSplitting, value);
    }

    /// <summary>When true, back up to a directory instead of optical disc.</summary>
    public bool IsDirectoryMode
    {
        get => _isDirectoryMode;
        set
        {
            if (SetProperty(ref _isDirectoryMode, value))
            {
                // Reset plan when switching modes.
                IsPlanReady = false;
                _plan = null;
                PlanSummary = "";
            }
        }
    }

    /// <summary>Target directory path for directory-mode backups.</summary>
    public string TargetDirectoryPath
    {
        get => _targetDirectoryPath;
        set => SetProperty(ref _targetDirectoryPath, value);
    }

    /// <summary>Whether to create a subdirectory under the target directory.</summary>
    public bool CreateSubdirectory
    {
        get => _createSubdirectory;
        set => SetProperty(ref _createSubdirectory, value);
    }

    /// <summary>Name of the subdirectory to create under the target directory.</summary>
    public string SubdirectoryName
    {
        get => _subdirectoryName;
        set => SetProperty(ref _subdirectoryName, value);
    }

    /// <summary>
    /// Comma-separated list of file extensions to exclude (e.g. ".log, .tmp, .bak").
    /// </summary>
    public string ExcludedExtensions
    {
        get => _excludedExtensions;
        set => SetProperty(ref _excludedExtensions, value);
    }

    /// <summary>Whether to enable file-level deduplication (identical files stored once).</summary>
    public bool EnableFileDeduplication
    {
        get => _enableFileDeduplication;
        set => SetProperty(ref _enableFileDeduplication, value);
    }

    /// <summary>Whether to enable block-level deduplication (detect partial file similarity).</summary>
    public bool EnableDeduplication
    {
        get => _enableDeduplication;
        set => SetProperty(ref _enableDeduplication, value);
    }

    /// <summary>Block size in KB for deduplication as a text field.</summary>
    public string DeduplicationBlockSizeKb
    {
        get => _deduplicationBlockSizeKb;
        set => SetProperty(ref _deduplicationBlockSizeKb, value);
    }

    // --- Schedule options (directory mode) ---

    /// <summary>Whether automated backups are enabled for this set.</summary>
    public bool ScheduleEnabled
    {
        get => _scheduleEnabled;
        set => SetProperty(ref _scheduleEnabled, value);
    }

    public ScheduleMode ScheduleMode
    {
        get => _scheduleMode;
        set => SetProperty(ref _scheduleMode, value);
    }

    public ScheduleMode[] ScheduleModeOptions { get; } = Enum.GetValues<ScheduleMode>();

    /// <summary>Hours between backups (Interval mode) as a text field.</summary>
    public string ScheduleIntervalHours
    {
        get => _scheduleIntervalHours;
        set => SetProperty(ref _scheduleIntervalHours, value);
    }

    /// <summary>Hour of day for daily backups (0–23).</summary>
    public int ScheduleDailyHour
    {
        get => _scheduleDailyHour;
        set => SetProperty(ref _scheduleDailyHour, value);
    }

    /// <summary>Minute for daily backups (0–59).</summary>
    public int ScheduleDailyMinute
    {
        get => _scheduleDailyMinute;
        set => SetProperty(ref _scheduleDailyMinute, value);
    }

    /// <summary>Debounce delay in seconds (Continuous mode) as a text field.</summary>
    public string ScheduleDebounceSeconds
    {
        get => _scheduleDebounceSeconds;
        set => SetProperty(ref _scheduleDebounceSeconds, value);
    }

    /// <summary>Configurable version retention tiers for directory-mode backups (legacy/default).</summary>
    public ObservableCollection<RetentionTierViewModel> RetentionTiers { get; }

    /// <summary>Named version tier sets passed through from source selection.</summary>
    public ObservableCollection<TierSetViewModel> TierSets { get; }

    public FilesystemType FilesystemType
    {
        get => _filesystemType;
        set => SetProperty(ref _filesystemType, value);
    }

    /// <summary>
    /// Capacity override in GB as a string. Empty or whitespace means auto-detect.
    /// Converts to/from bytes internally.
    /// </summary>
    public string CapacityOverrideGb
    {
        get => _capacityOverrideGb;
        set => SetProperty(ref _capacityOverrideGb, value);
    }

    /// <summary>
    /// Returns the parsed capacity override in bytes, or null if the field is blank or invalid.
    /// </summary>
    internal long? GetCapacityOverrideBytes()
    {
        if (string.IsNullOrWhiteSpace(_capacityOverrideGb))
            return null;

        if (double.TryParse(_capacityOverrideGb, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double gb) && gb > 0)
        {
            return (long)(gb * 1024 * 1024 * 1024);
        }

        return null;
    }

    public ZipMode[] ZipModeOptions { get; } = Enum.GetValues<ZipMode>();
    public FilesystemType[] FilesystemTypeOptions { get; } = Enum.GetValues<FilesystemType>();

    // --- Plan state ---

    public bool IsPlanning
    {
        get => _isPlanning;
        set => SetProperty(ref _isPlanning, value);
    }

    public bool IsPlanReady
    {
        get => _isPlanReady;
        set => SetProperty(ref _isPlanReady, value);
    }

    public string PlanSummary
    {
        get => _planSummary;
        set => SetProperty(ref _planSummary, value);
    }

    public string MediaInfoText
    {
        get => _mediaInfoText;
        set => SetProperty(ref _mediaInfoText, value);
    }

    // --- Commands ---

    public ICommand PlanCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand BrowseTargetDirectoryCommand { get; }
    public ICommand AddTierCommand { get; }

    // --- Logic ---

    private void AddRetentionTier()
    {
        var tier = new RetentionTierViewModel();
        tier.RemoveRequested += t => RetentionTiers.Remove(t);
        RetentionTiers.Add(tier);
    }

    private async Task LoadMediaInfoAsync()
    {
        try
        {
            var recorderIds = Burner.GetRecorderIds();
            if (recorderIds.Count == 0)
            {
                MediaInfoText = "No disc recorder detected. Insert a disc and try again.";
                return;
            }

            var info = await Burner.GetMediaInfoAsync(recorderIds[0]);
            if (info.MediaType == MediaType.Unknown)
            {
                MediaInfoText = $"Recorder: {info.RecorderName}\nNo disc detected. Insert a disc.";
                return;
            }

            string rewritable = info.IsRewritable ? " (rewritable)" : "";
            MediaInfoText =
                $"Recorder: {info.RecorderName}\n" +
                $"Media: {info.MediaType}{rewritable}\n" +
                $"Capacity: {FormatBytes(info.TotalCapacityBytes)}\n" +
                $"Free: {FormatBytes(info.FreeSpaceBytes)}" +
                (info.IsBlank ? " (blank)" : $" ({info.SessionCount} session(s))");
        }
        catch (Exception ex)
        {
            MediaInfoText = $"Could not query media: {ex.Message}";
        }
    }

    private async Task PlanAsync()
    {
        IsPlanning = true;
        IsPlanReady = false;
        PlanSummary = "Scanning files and computing backup plan...";

        try
        {
            // Compute the effective target directory, appending subdirectory if specified.
            string? effectiveTargetDir = null;
            if (IsDirectoryMode)
            {
                effectiveTargetDir = TargetDirectoryPath;
                if (CreateSubdirectory && !string.IsNullOrWhiteSpace(SubdirectoryName))
                    effectiveTargetDir = System.IO.Path.Combine(effectiveTargetDir, SubdirectoryName.Trim());
            }

            var job = new BackupJob
            {
                Sources = Sources,
                ZipMode = ZipMode,
                FilesystemType = FilesystemType,
                CapacityOverrideBytes = GetCapacityOverrideBytes(),
                VerifyAfterBurn = VerifyAfterBurn,
                IncludeCatalogOnDisc = IncludeCatalogOnDisc,
                AllowFileSplitting = AllowFileSplitting,
                TargetDirectory = effectiveTargetDir,
                CreateSubdirectory = CreateSubdirectory,
                SubdirectoryName = CreateSubdirectory ? SubdirectoryName?.Trim() : null,
                EnableFileDeduplication = EnableFileDeduplication,
                EnableDeduplication = EnableDeduplication,
                ExcludedExtensions = ParseExclusionPatterns(ExcludedExtensions),
            };

            // Parse deduplication block size.
            if (int.TryParse(DeduplicationBlockSizeKb, out int blockKb) && blockKb > 0)
                job.DeduplicationBlockSize = blockKb * 1024;

            // Convert retention tier ViewModels to models.
            job.RetentionTiers = RetentionTiers.Select(t => t.ToModel()).ToList();
            job.TierSets = TierSets.Select(ts => ts.ToModel()).ToList();

            if (IsDirectoryMode)
            {
                if (string.IsNullOrWhiteSpace(TargetDirectoryPath))
                {
                    PlanSummary = "Please select a target directory.";
                    return;
                }

                if (DirectoryBackup is null)
                {
                    PlanSummary = "Directory backup service not available.";
                    return;
                }

                // Run the heavy scan/diff work on a background thread so the
                // UI stays responsive and the spinner is visible.
                var (diff, totalBytes, totalFiles) = await Task.Run(
                    () => DirectoryBackup.PlanAsync(job, CancellationToken.None));

                int newCount = diff.NewFiles.Count;
                int changedCount = diff.ChangedFiles.Count;
                int deletedCount = diff.DeletedFiles.Count;

                // Create a BackupPlan with empty disc allocations for directory mode.
                _plan = new BackupPlan
                {
                    Job = job,
                    Diff = diff,
                    DiscAllocations = [],
                    TotalDiscsRequired = 0,
                    TotalBytes = totalBytes,
                };

                var summary =
                    $"Files to back up: {totalFiles:N0}\n" +
                    $"  New: {newCount:N0}    Changed: {changedCount:N0}    Deleted: {deletedCount:N0}\n" +
                    $"Total size: {FormatBytes(totalBytes)}\n" +
                    $"Target: {effectiveTargetDir}";

                if (totalFiles == 0)
                {
                    summary += "\n\nNothing to back up. All files are already current.";
                }

                PlanSummary = summary;
                IsPlanReady = totalFiles > 0;
            }
            else
            {
                // Run the heavy scan/diff work on a background thread.
                _plan = await Task.Run(() => Orchestrator.PlanAsync(job));

                int newCount = _plan.Diff.NewFiles.Count;
                int changedCount = _plan.Diff.ChangedFiles.Count;
                int deletedCount = _plan.Diff.DeletedFiles.Count;
                int totalFiles = newCount + changedCount;

                PlanSummary =
                    $"Files to back up: {totalFiles:N0}\n" +
                    $"  New: {newCount:N0}    Changed: {changedCount:N0}    Deleted: {deletedCount:N0}\n" +
                    $"Total size: {FormatBytes(_plan.TotalBytes)}\n" +
                    $"Discs required: {_plan.TotalDiscsRequired}";

                if (totalFiles == 0)
                {
                    PlanSummary += "\n\nNothing to back up. All files are already current.";
                }

                IsPlanReady = totalFiles > 0;
            }

            // Notify listeners so the backup set can be saved.
            if (IsPlanReady)
                PlanCompleted?.Invoke(job);
        }
        catch (Exception ex)
        {
            PlanSummary = $"Planning failed: {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            IsPlanning = false;
        }
    }

    private void OnStartRequested()
    {
        if (_plan is not null)
            StartRequested?.Invoke(_plan);
    }

    private void BrowseTargetDirectory()
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select target directory for backup",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };

        if (!string.IsNullOrWhiteSpace(TargetDirectoryPath) && System.IO.Directory.Exists(TargetDirectoryPath))
            dialog.SelectedPath = TargetDirectoryPath;

        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            TargetDirectoryPath = dialog.SelectedPath;
        }
    }

    /// <summary>
    /// Build a <see cref="BackupSchedule"/> from the current UI state.
    /// Returns null if scheduling is disabled.
    /// </summary>
    internal BackupSchedule? BuildSchedule()
    {
        if (!ScheduleEnabled) return null;

        double intervalHours = 24;
        if (double.TryParse(ScheduleIntervalHours, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double h) && h > 0)
            intervalHours = h;

        int debounce = 60;
        if (int.TryParse(ScheduleDebounceSeconds, out int d) && d > 0)
            debounce = d;

        return new BackupSchedule
        {
            Enabled = true,
            Mode = ScheduleMode,
            IntervalHours = intervalHours,
            DailyHour = ScheduleDailyHour,
            DailyMinute = ScheduleDailyMinute,
            DebounceSeconds = debounce,
        };
    }

    /// <summary>
    /// Parse an exclusion pattern string into a normalized list. Splits on
    /// newlines only — commas and semicolons are allowed in patterns since
    /// they are valid characters in Windows file names.
    /// </summary>
    internal static List<string> ParseExclusionPatterns(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return [];

        return input
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Format an exclusion pattern list back to a display string (one per line).
    /// </summary>
    internal static string FormatExclusionPatterns(List<string> patterns)
    {
        return string.Join("\n", patterns);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        double size = bytes;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return i == 0 ? $"{size:N0} {units[i]}" : $"{size:N1} {units[i]}";
    }
}
