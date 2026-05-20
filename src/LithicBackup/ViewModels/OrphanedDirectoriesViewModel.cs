using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using LithicBackup.Core;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.ViewModels;

/// <summary>
/// Shows directories in a backup set that are no longer covered by the
/// current source roots — either because the user removed them or because
/// the directory was deleted from disk.  Also auto-detects files matching
/// configured exclusion patterns and excess file versions that no longer
/// fit the retention tier rules.  Supports scanning for additional files
/// that match user-typed exclusion patterns so the user can purge them
/// from the catalog.
/// </summary>
public class OrphanedDirectoriesViewModel : ViewModelBase
{
    private readonly ICatalogRepository _catalog;
    private readonly BackupSet _backupSet;
    private bool _isLoading;
    private bool _isPurging;
    private string _summaryText = "Loading...";
    private string _exclusionPatterns = "";

    /// <summary>Cached active files from the catalog, loaded once during init.</summary>
    private List<FileRecord>? _activeFiles;

    private string _purgeStatusText = "";

    /// <summary>Task that completes when initial data loading finishes.</summary>
    private readonly Task _loadTask;

    public event Action? DoneRequested;

    public OrphanedDirectoriesViewModel(ICatalogRepository catalog, BackupSet backupSet)
    {
        _catalog = catalog;
        _backupSet = backupSet;

        Items = [];
        PurgeSelectedCommand = new RelayCommand(_ => PurgeSelected(), _ => !IsPurging && Items.Any(i => i.IsSelected));
        ScanExcludedCommand = new RelayCommand(_ => _ = ScanForExcludedAsync(), _ => !IsLoading && !IsPurging);
        CloseCommand = new RelayCommand(_ => DoneRequested?.Invoke());

        _loadTask = LoadAsync();
    }

    /// <summary>
    /// Await initial data loading. Used by callers that want to show a wait
    /// cursor until the view is ready (e.g. <see cref="MainViewModel"/>).
    /// </summary>
    internal Task WaitForLoadAsync() => _loadTask;

    public ObservableCollection<OrphanedDirectoryItem> Items { get; }

    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    public bool IsPurging
    {
        get => _isPurging;
        set => SetProperty(ref _isPurging, value);
    }

    public string SummaryText
    {
        get => _summaryText;
        set => SetProperty(ref _summaryText, value);
    }

    /// <summary>
    /// Comma-separated exclusion patterns. Files in the catalog matching these
    /// patterns are shown as candidates for purging.
    /// </summary>
    public string ExclusionPatterns
    {
        get => _exclusionPatterns;
        set => SetProperty(ref _exclusionPatterns, value);
    }

    /// <summary>
    /// Header checkbox — tristate aggregate of all items.
    /// </summary>
    public bool? IsAllSelected
    {
        get
        {
            if (Items.Count == 0) return false;
            bool allTrue = Items.All(i => i.IsSelected);
            bool allFalse = Items.All(i => !i.IsSelected);
            if (allTrue) return true;
            if (allFalse) return false;
            return null;
        }
        set
        {
            bool target = value ?? true;
            foreach (var item in Items)
                item.IsSelected = target;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Live progress text shown during purge (e.g. "Purging 3/12: D:\Photos\Old").
    /// </summary>
    public string PurgeStatusText
    {
        get => _purgeStatusText;
        set => SetProperty(ref _purgeStatusText, value);
    }

    public ICommand PurgeSelectedCommand { get; }
    public ICommand ScanExcludedCommand { get; }
    public ICommand CloseCommand { get; }

    // ------------------------------------------------------------------

    private async Task LoadAsync()
    {
        IsLoading = true;

        try
        {
            var files = await _catalog.GetAllFilesForBackupSetAsync(_backupSet.Id);
            _activeFiles = files.Where(f => !f.IsDeleted).ToList();
            var sourceRoots = _backupSet.SourceRoots;

            // Group files by parent directory.
            var dirGroups = _activeFiles
                .GroupBy(f => Path.GetDirectoryName(f.SourcePath) ?? f.SourcePath,
                         StringComparer.OrdinalIgnoreCase)
                .ToList();

            var orphaned = new List<OrphanedDirectoryItem>();

            foreach (var group in dirGroups)
            {
                string dir = group.Key;
                bool underSourceRoot = sourceRoots.Any(root =>
                    dir.StartsWith(root, StringComparison.OrdinalIgnoreCase));

                if (!underSourceRoot)
                {
                    orphaned.Add(new OrphanedDirectoryItem
                    {
                        DirectoryPath = dir,
                        Reason = OrphanedReason.RemovedFromSources,
                        FileCount = group.Count(),
                        TotalSizeBytes = group.Sum(f => f.SizeBytes),
                    });
                }
                else if (!Directory.Exists(dir))
                {
                    orphaned.Add(new OrphanedDirectoryItem
                    {
                        DirectoryPath = dir,
                        Reason = OrphanedReason.DeletedFromDisk,
                        FileCount = group.Count(),
                        TotalSizeBytes = group.Sum(f => f.SizeBytes),
                    });
                }
            }

            // Collapse children into their highest orphaned ancestor.
            // Sort shortest paths first so parents come before children.
            orphaned.Sort((a, b) => string.Compare(a.DirectoryPath, b.DirectoryPath, StringComparison.OrdinalIgnoreCase));

            var collapsed = new List<OrphanedDirectoryItem>();
            foreach (var item in orphaned)
            {
                var parent = collapsed.FirstOrDefault(c =>
                    item.DirectoryPath.StartsWith(c.DirectoryPath + "\\", StringComparison.OrdinalIgnoreCase));

                if (parent is not null)
                {
                    // Merge into parent.
                    parent.FileCount += item.FileCount;
                    parent.TotalSizeBytes += item.TotalSizeBytes;
                    // If any child is deleted-from-disk but parent was removed-from-sources,
                    // keep the parent's reason (the root cause is removal).
                }
                else
                {
                    collapsed.Add(item);
                }
            }

            // --- Phase 2: Auto-detect files matching configured exclusion patterns ---
            var excludedItems = await Task.Run(() => DetectExcludedFiles(collapsed));

            // --- Phase 3: Auto-detect excess file versions from retention tiers ---
            var excessItems = await Task.Run(() => DetectExcessVersions(collapsed, excludedItems));

            Items.Clear();
            foreach (var item in collapsed
                .Concat(excludedItems)
                .Concat(excessItems)
                .OrderBy(i => i.DirectoryPath, StringComparer.OrdinalIgnoreCase))
            {
                item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(IsAllSelected));
                Items.Add(item);
            }

            UpdateSummaryText();
        }
        catch (Exception ex)
        {
            SummaryText = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ------------------------------------------------------------------
    // Phase 2: Configured exclusion detection
    // ------------------------------------------------------------------

    /// <summary>
    /// Detect files in the catalog that match the backup set's configured
    /// exclusion patterns (global excluded extensions + tier sets with 0 tiers).
    /// Mirrors the logic of DirectoryBackupService.BuildExclusionFilter.
    /// </summary>
    private List<OrphanedDirectoryItem> DetectExcludedFiles(
        List<OrphanedDirectoryItem> orphanedDirs)
    {
        if (_activeFiles is null) return [];

        var jobOptions = _backupSet.JobOptions;
        if (jobOptions is null) return [];

        // Build exclusion filter — same logic as DirectoryBackupService.BuildExclusionFilter.
        var globalFilter = jobOptions.ExcludedExtensions.Count > 0
            ? GlobMatcher.CreateFilter(jobOptions.ExcludedExtensions) : null;

        Func<string, VersionTierSet>? tierResolver = null;
        if (jobOptions.TierSets.Count > 0)
        {
            var resolver = VersionTierSet.BuildTierResolver(jobOptions.TierSets);
            bool hasExclusionTierSet = jobOptions.TierSets.Any(ts =>
                ts.Tiers.Count == 0
                && ts.FilePatterns.Count > 0
                && !string.Equals(ts.Name, "Default", StringComparison.OrdinalIgnoreCase));
            if (hasExclusionTierSet)
                tierResolver = resolver;
        }

        if (globalFilter is null && tierResolver is null)
            return [];

        Func<string, bool> exclusionFilter = path =>
        {
            if (globalFilter?.Invoke(path) ?? false)
                return true;
            if (tierResolver is not null && tierResolver(path).Tiers.Count == 0)
                return true;
            return false;
        };

        // Build set of orphaned directory prefixes to skip (files there are
        // already covered by orphaned directory items).
        var orphanedDirPrefixes = orphanedDirs
            .Select(i => i.DirectoryPath + "\\")
            .ToList();

        var excluded = _activeFiles
            .Where(f =>
            {
                if (orphanedDirPrefixes.Any(p =>
                    f.SourcePath.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    return false;

                return exclusionFilter(f.SourcePath);
            })
            .ToList();

        if (excluded.Count == 0)
            return [];

        // Group by parent directory.
        var results = new List<OrphanedDirectoryItem>();
        var dirGroups = excluded
            .GroupBy(f => Path.GetDirectoryName(f.SourcePath) ?? f.SourcePath,
                     StringComparer.OrdinalIgnoreCase);

        foreach (var group in dirGroups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            results.Add(new OrphanedDirectoryItem
            {
                DirectoryPath = group.Key,
                Reason = OrphanedReason.MatchesConfiguredExclusion,
                FileCount = group.Count(),
                TotalSizeBytes = group.Sum(f => f.SizeBytes),
                MatchingSourcePaths = group.Select(f => f.SourcePath).ToList(),
            });
        }

        return results;
    }

    // ------------------------------------------------------------------
    // Phase 3: Excess version detection
    // ------------------------------------------------------------------

    /// <summary>
    /// Detect file versions that exceed the configured retention tier limits.
    /// Replicates the core logic of VersionRetentionService.ComputeRetentionAsync
    /// using the already-loaded in-memory file list.
    /// </summary>
    private List<OrphanedDirectoryItem> DetectExcessVersions(
        List<OrphanedDirectoryItem> orphanedDirs,
        List<OrphanedDirectoryItem> excludedItems)
    {
        if (_activeFiles is null) return [];

        var jobOptions = _backupSet.JobOptions;
        if (jobOptions is null) return [];

        // Build per-file tier selector: file path → retention tiers.
        Func<string, IReadOnlyList<VersionRetentionTier>> tierSelector;

        if (jobOptions.TierSets.Count > 0)
        {
            var resolver = VersionTierSet.BuildTierResolver(jobOptions.TierSets);
            tierSelector = path => resolver(path).Tiers;
        }
        else if (jobOptions.RetentionTiers.Count > 0)
        {
            var flatTiers = jobOptions.RetentionTiers;
            tierSelector = _ => flatTiers;
        }
        else
        {
            return []; // No retention rules configured.
        }

        // Build skip-sets: files in orphaned dirs or matched by exclusion filter.
        var orphanedDirPrefixes = orphanedDirs
            .Select(i => i.DirectoryPath + "\\")
            .ToList();
        var excludedPaths = new HashSet<string>(
            excludedItems
                .Where(i => i.MatchingSourcePaths is not null)
                .SelectMany(i => i.MatchingSourcePaths!),
            StringComparer.OrdinalIgnoreCase);

        // Filter active files to only those not already flagged.
        var eligibleFiles = _activeFiles
            .Where(f =>
            {
                if (orphanedDirPrefixes.Any(p =>
                    f.SourcePath.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                    return false;
                if (excludedPaths.Contains(f.SourcePath))
                    return false;
                return true;
            })
            .ToList();

        var now = DateTime.UtcNow;
        var excessRecords = new List<FileRecord>();

        // Group by source path — same approach as VersionRetentionService.
        var groupedByPath = eligibleFiles
            .GroupBy(f => f.SourcePath, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groupedByPath)
        {
            // Sort versions by BackedUpUtc descending (newest first).
            var versions = group
                .OrderByDescending(f => f.BackedUpUtc)
                .ToList();

            if (versions.Count <= 1)
                continue; // Only one version, nothing to trim.

            var tiers = tierSelector(group.Key);
            if (tiers.Count == 0)
                continue; // 0 tiers = no retention rules (excluded or no history).

            // Never delete the most recent version of any file.
            long newestId = versions[0].Id;

            // Walk tiers from youngest to oldest.
            var sortedTiers = tiers
                .OrderBy(t => t.MaxAge ?? TimeSpan.MaxValue)
                .ToList();

            var processed = new HashSet<long>();
            TimeSpan previousBoundary = TimeSpan.Zero;

            foreach (var tier in sortedTiers)
            {
                TimeSpan upperBoundary = tier.MaxAge ?? TimeSpan.MaxValue;

                // Find versions in this tier's age range.
                var tierVersions = versions
                    .Where(v => !processed.Contains(v.Id))
                    .Where(v =>
                    {
                        var age = now - v.BackedUpUtc;
                        return age >= previousBoundary && age < upperBoundary;
                    })
                    .OrderByDescending(v => v.BackedUpUtc) // Keep newest first.
                    .ToList();

                if (tier.MaxVersions.HasValue && tierVersions.Count > tier.MaxVersions.Value)
                {
                    // Keep MaxVersions newest, mark rest for deletion.
                    int toKeep = tier.MaxVersions.Value;
                    for (int i = 0; i < tierVersions.Count; i++)
                    {
                        processed.Add(tierVersions[i].Id);
                        if (i >= toKeep && tierVersions[i].Id != newestId)
                        {
                            excessRecords.Add(tierVersions[i]);
                        }
                    }
                }
                else
                {
                    // Unlimited or within limit — keep all.
                    foreach (var v in tierVersions)
                        processed.Add(v.Id);
                }

                previousBoundary = upperBoundary;
            }
        }

        if (excessRecords.Count == 0)
            return [];

        // Group excess records by parent directory.
        var results = new List<OrphanedDirectoryItem>();
        var dirGroups = excessRecords
            .GroupBy(f => Path.GetDirectoryName(f.SourcePath) ?? f.SourcePath,
                     StringComparer.OrdinalIgnoreCase);

        foreach (var group in dirGroups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            results.Add(new OrphanedDirectoryItem
            {
                DirectoryPath = group.Key,
                Reason = OrphanedReason.ExcessVersion,
                FileCount = group.Count(),
                TotalSizeBytes = group.Sum(f => f.SizeBytes),
                ExcessVersionRecords = group.ToList(),
            });
        }

        return results;
    }

    // ------------------------------------------------------------------
    // Manual exclusion scan
    // ------------------------------------------------------------------

    /// <summary>
    /// Scan the catalog for files matching the exclusion patterns and add them
    /// to the list as purgeable items.
    /// </summary>
    private async Task ScanForExcludedAsync()
    {
        if (_activeFiles is null)
            return;

        // Remove previous manual exclusion-pattern items (keep auto-detected ones).
        for (int i = Items.Count - 1; i >= 0; i--)
        {
            if (Items[i].Reason == OrphanedReason.MatchesExclusionPattern)
                Items.RemoveAt(i);
        }

        var patterns = ParsePatterns(ExclusionPatterns);
        if (patterns.Count == 0)
        {
            UpdateSummaryText();
            return;
        }

        var filter = GlobMatcher.CreateFilter(patterns);
        if (filter is null)
        {
            UpdateSummaryText();
            return;
        }

        IsLoading = true;
        SummaryText = "Scanning for excluded files...";

        try
        {
            // Build set of directories already shown as orphaned so we don't
            // double-count files.
            var orphanedDirPrefixes = Items
                .Where(i => i.Reason is OrphanedReason.RemovedFromSources or OrphanedReason.DeletedFromDisk)
                .Select(i => i.DirectoryPath + "\\")
                .ToList();

            // Also skip files already flagged by auto-detected exclusions.
            var alreadyFlaggedPaths = new HashSet<string>(
                Items
                    .Where(i => i.Reason == OrphanedReason.MatchesConfiguredExclusion && i.MatchingSourcePaths is not null)
                    .SelectMany(i => i.MatchingSourcePaths!),
                StringComparer.OrdinalIgnoreCase);

            // Run the filter on a background thread — could be thousands of files.
            var excluded = await Task.Run(() =>
            {
                return _activeFiles
                    .Where(f =>
                    {
                        // Skip files already covered by an orphaned directory.
                        if (orphanedDirPrefixes.Any(p =>
                            f.SourcePath.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
                            return false;

                        // Skip files already covered by auto-detected exclusions.
                        if (alreadyFlaggedPaths.Contains(f.SourcePath))
                            return false;

                        return filter(f.SourcePath);
                    })
                    .ToList();
            });

            if (excluded.Count == 0)
            {
                UpdateSummaryText();
                return;
            }

            // Group by parent directory.
            var dirGroups = excluded
                .GroupBy(f => Path.GetDirectoryName(f.SourcePath) ?? f.SourcePath,
                         StringComparer.OrdinalIgnoreCase);

            foreach (var group in dirGroups.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var item = new OrphanedDirectoryItem
                {
                    DirectoryPath = group.Key,
                    Reason = OrphanedReason.MatchesExclusionPattern,
                    FileCount = group.Count(),
                    TotalSizeBytes = group.Sum(f => f.SizeBytes),
                    MatchingSourcePaths = group.Select(f => f.SourcePath).ToList(),
                };
                item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(IsAllSelected));
                Items.Add(item);
            }

            UpdateSummaryText();
        }
        catch (Exception ex)
        {
            SummaryText = $"Scan failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ------------------------------------------------------------------
    // Purge
    // ------------------------------------------------------------------

    private async void PurgeSelected()
    {
        var selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;

        IsPurging = true;
        SummaryText = "Purging...";
        PurgeStatusText = "";

        try
        {
            // Snapshot data needed by the background thread before leaving
            // the UI thread.  MatchingSourcePaths, ExcessVersionRecords, and
            // DirectoryPath are plain properties, safe to capture.
            var workItems = selected.Select(item => new
            {
                item.DirectoryPath,
                item.Reason,
                item.MatchingSourcePaths,
                item.ExcessVersionRecords,
            }).ToList();

            int backupSetId = _backupSet.Id;
            var progress = new Progress<string>(status => PurgeStatusText = status);

            // Run all DB work on a background thread — the catalog methods
            // are synchronous (ExecuteNonQuery / Task.FromResult) and would
            // otherwise freeze the UI for the entire purge.
            int totalPurged = await Task.Run(() =>
            {
                var tx = _catalog.BeginTransactionAsync().GetAwaiter().GetResult();
                int purged = 0;

                try
                {
                    for (int i = 0; i < workItems.Count; i++)
                    {
                        var wi = workItems[i];

                        var dirName = Path.GetFileName(wi.DirectoryPath.TrimEnd('\\'));
                        ((IProgress<string>)progress).Report(
                            $"Purging {i + 1:N0}/{workItems.Count:N0}: {dirName}");

                        if (wi.Reason == OrphanedReason.ExcessVersion
                            && wi.ExcessVersionRecords is not null)
                        {
                            // Mark individual excess version records as deleted.
                            foreach (var record in wi.ExcessVersionRecords)
                            {
                                record.IsDeleted = true;
                                _catalog.UpdateFileRecordAsync(record).GetAwaiter().GetResult();
                                purged++;
                            }
                        }
                        else if (wi.Reason is OrphanedReason.MatchesExclusionPattern
                                           or OrphanedReason.MatchesConfiguredExclusion
                            && wi.MatchingSourcePaths is not null)
                        {
                            purged += _catalog.MarkFilesDeletedBySourcePathsAsync(
                                backupSetId, wi.MatchingSourcePaths).GetAwaiter().GetResult();
                        }
                        else
                        {
                            purged += _catalog.MarkFilesDeletedByDirectoryAsync(
                                backupSetId, wi.DirectoryPath).GetAwaiter().GetResult();
                        }
                    }

                    tx.Complete();
                }
                finally
                {
                    tx.Dispose();
                }

                // Clean the cached active-file list on the same thread
                // (no UI objects touched).
                if (_activeFiles is not null)
                {
                    var purgedPaths = new HashSet<string>(
                        workItems.Where(w => w.MatchingSourcePaths is not null)
                                 .SelectMany(w => w.MatchingSourcePaths!),
                        StringComparer.OrdinalIgnoreCase);
                    var purgedDirs = workItems
                        .Where(w => w.Reason is OrphanedReason.RemovedFromSources or OrphanedReason.DeletedFromDisk)
                        .Select(w => w.DirectoryPath + "\\")
                        .ToList();
                    var purgedRecordIds = new HashSet<long>(
                        workItems.Where(w => w.ExcessVersionRecords is not null)
                                 .SelectMany(w => w.ExcessVersionRecords!)
                                 .Select(r => r.Id));

                    _activeFiles.RemoveAll(f =>
                        purgedPaths.Contains(f.SourcePath)
                        || purgedDirs.Any(d => f.SourcePath.StartsWith(d, StringComparison.OrdinalIgnoreCase))
                        || purgedRecordIds.Contains(f.Id));
                }

                return purged;
            });

            // Back on the UI thread — update the observable collection.
            foreach (var item in selected)
                Items.Remove(item);

            SummaryText = $"Purged {totalPurged:N0} file record(s). {Items.Count} item{(Items.Count == 1 ? "" : "s")} remaining.";
        }
        catch (Exception ex)
        {
            SummaryText = $"Purge failed: {ex.Message}";
        }
        finally
        {
            PurgeStatusText = "";
            IsPurging = false;
        }
    }

    // ------------------------------------------------------------------

    private void UpdateSummaryText()
    {
        if (Items.Count == 0)
        {
            SummaryText = "No orphaned directories, excluded files, or excess versions found.";
            return;
        }

        int orphanedCount = Items.Count(i => i.Reason is OrphanedReason.RemovedFromSources or OrphanedReason.DeletedFromDisk);
        int excludedCount = Items.Count(i => i.Reason is OrphanedReason.MatchesExclusionPattern or OrphanedReason.MatchesConfiguredExclusion);
        int excessCount = Items.Count(i => i.Reason == OrphanedReason.ExcessVersion);

        var parts = new List<string>();
        if (orphanedCount > 0)
            parts.Add($"{orphanedCount} orphaned director{(orphanedCount == 1 ? "y" : "ies")}");
        if (excludedCount > 0)
        {
            int totalExcludedFiles = Items
                .Where(i => i.Reason is OrphanedReason.MatchesExclusionPattern or OrphanedReason.MatchesConfiguredExclusion)
                .Sum(i => i.FileCount);
            parts.Add($"{totalExcludedFiles:N0} excluded file{(totalExcludedFiles == 1 ? "" : "s")} in {excludedCount} director{(excludedCount == 1 ? "y" : "ies")}");
        }
        if (excessCount > 0)
        {
            int totalExcessVersions = Items
                .Where(i => i.Reason == OrphanedReason.ExcessVersion)
                .Sum(i => i.FileCount);
            parts.Add($"{totalExcessVersions:N0} excess version{(totalExcessVersions == 1 ? "" : "s")} in {excessCount} director{(excessCount == 1 ? "y" : "ies")}");
        }

        SummaryText = string.Join(", ", parts) + " found.";
    }

    private static List<string> ParsePatterns(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return [];

        return input
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

// ------------------------------------------------------------------

public enum OrphanedReason
{
    /// <summary>The directory still exists on disk but is no longer in the backup sources.</summary>
    RemovedFromSources,

    /// <summary>The directory no longer exists on disk.</summary>
    DeletedFromDisk,

    /// <summary>Files in this directory match a user-typed exclusion pattern (manual scan).</summary>
    MatchesExclusionPattern,

    /// <summary>Files in this directory match the backup set's configured exclusion rules
    /// (global excluded extensions or tier sets with 0 tiers).</summary>
    MatchesConfiguredExclusion,

    /// <summary>File versions that exceed the configured retention tier limits.</summary>
    ExcessVersion,
}

/// <summary>
/// One orphaned directory entry in the list.
/// </summary>
public class OrphanedDirectoryItem : ViewModelBase
{
    private bool _isSelected;

    public string DirectoryPath { get; set; } = string.Empty;
    public OrphanedReason Reason { get; set; }
    public int FileCount { get; set; }
    public long TotalSizeBytes { get; set; }

    /// <summary>
    /// For <see cref="OrphanedReason.MatchesExclusionPattern"/> and
    /// <see cref="OrphanedReason.MatchesConfiguredExclusion"/> items, the specific
    /// source paths of matching files.  Used for targeted purging (instead of
    /// deleting all files under the directory).
    /// </summary>
    public List<string>? MatchingSourcePaths { get; set; }

    /// <summary>
    /// For <see cref="OrphanedReason.ExcessVersion"/> items, the specific
    /// file records (individual versions) that exceed the retention tier limits.
    /// Used for targeted purging of individual version records.
    /// </summary>
    public List<FileRecord>? ExcessVersionRecords { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string ReasonText => Reason switch
    {
        OrphanedReason.RemovedFromSources => "Removed from sources",
        OrphanedReason.DeletedFromDisk => "Deleted from disk",
        OrphanedReason.MatchesExclusionPattern => "Matches exclusion pattern",
        OrphanedReason.MatchesConfiguredExclusion => "Matches configured exclusion",
        OrphanedReason.ExcessVersion => "Excess version (retention)",
        _ => "Unknown",
    };

    public string SizeText => FormatBytes(TotalSizeBytes);

    private static string FormatBytes(long bytes) => $"{bytes:N0}";
}
