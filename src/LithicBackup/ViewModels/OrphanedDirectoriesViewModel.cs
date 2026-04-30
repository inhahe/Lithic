using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using LithicBackup.Core.Interfaces;
using LithicBackup.Core.Models;

namespace LithicBackup.ViewModels;

/// <summary>
/// Shows directories in a backup set that are no longer covered by the
/// current source roots — either because the user removed them or because
/// the directory was deleted from disk.  The user can select entries and
/// purge them (soft-delete the file records) from the catalog.
/// </summary>
public class OrphanedDirectoriesViewModel : ViewModelBase
{
    private readonly ICatalogRepository _catalog;
    private readonly BackupSet _backupSet;
    private bool _isLoading;
    private bool _isPurging;
    private string _summaryText = "Loading...";

    public event Action? DoneRequested;

    public OrphanedDirectoriesViewModel(ICatalogRepository catalog, BackupSet backupSet)
    {
        _catalog = catalog;
        _backupSet = backupSet;

        Items = [];
        PurgeSelectedCommand = new RelayCommand(_ => PurgeSelected(), _ => !IsPurging && Items.Any(i => i.IsSelected));
        CloseCommand = new RelayCommand(_ => DoneRequested?.Invoke());

        _ = LoadAsync();
    }

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

    public ICommand PurgeSelectedCommand { get; }
    public ICommand CloseCommand { get; }

    // ------------------------------------------------------------------

    private async Task LoadAsync()
    {
        IsLoading = true;

        try
        {
            var files = await _catalog.GetAllFilesForBackupSetAsync(_backupSet.Id);
            var activeFiles = files.Where(f => !f.IsDeleted).ToList();
            var sourceRoots = _backupSet.SourceRoots;

            // Group files by parent directory.
            var dirGroups = activeFiles
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
                        Reason = Directory.Exists(dir)
                            ? OrphanedReason.RemovedFromSources
                            : OrphanedReason.DeletedFromDisk,
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

            Items.Clear();
            foreach (var item in collapsed.OrderBy(i => i.DirectoryPath, StringComparer.OrdinalIgnoreCase))
            {
                item.PropertyChanged += (_, _) => OnPropertyChanged(nameof(IsAllSelected));
                Items.Add(item);
            }

            SummaryText = Items.Count == 0
                ? "No orphaned directories found."
                : $"{Items.Count} orphaned director{(Items.Count == 1 ? "y" : "ies")} found.";
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

    private async void PurgeSelected()
    {
        var selected = Items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0) return;

        IsPurging = true;
        SummaryText = "Purging...";

        try
        {
            using var tx = await _catalog.BeginTransactionAsync();
            int totalPurged = 0;

            foreach (var item in selected)
            {
                int count = await _catalog.MarkFilesDeletedByDirectoryAsync(
                    _backupSet.Id, item.DirectoryPath);
                totalPurged += count;
            }

            (tx as IDisposable)?.Dispose(); // commit

            // Remove purged items from the list.
            foreach (var item in selected)
                Items.Remove(item);

            SummaryText = $"Purged {totalPurged} file record(s). {Items.Count} orphaned director{(Items.Count == 1 ? "y" : "ies")} remaining.";
        }
        catch (Exception ex)
        {
            SummaryText = $"Purge failed: {ex.Message}";
        }
        finally
        {
            IsPurging = false;
        }
    }
}

// ------------------------------------------------------------------

public enum OrphanedReason
{
    /// <summary>The directory still exists on disk but is no longer in the backup sources.</summary>
    RemovedFromSources,

    /// <summary>The directory no longer exists on disk.</summary>
    DeletedFromDisk,
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

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public string ReasonText => Reason switch
    {
        OrphanedReason.RemovedFromSources => "Removed from sources",
        OrphanedReason.DeletedFromDisk => "Deleted from disk",
        _ => "Unknown",
    };

    public string SizeText => FormatBytes(TotalSizeBytes);

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int i = 0;
        double size = bytes;
        while (size >= 1024 && i < units.Length - 1) { size /= 1024; i++; }
        return i == 0 ? $"{size:N0} {units[i]}" : $"{size:N1} {units[i]}";
    }
}
