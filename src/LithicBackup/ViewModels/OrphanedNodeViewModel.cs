using System.Collections.ObjectModel;
using System.Windows.Input;

namespace LithicBackup.ViewModels;

/// <summary>
/// A node in the orphaned-directories tree. Interior nodes are pure path
/// containers (no associated item).  Leaf nodes wrap an
/// <see cref="OrphanedDirectoryItem"/> which carries the metadata needed
/// for purging (matching paths, excess version records, etc.).
///
/// Supports tri-state checkboxes so partially-selected directories show an
/// indeterminate state.  Leaf nodes keep their wrapped item's
/// <see cref="OrphanedDirectoryItem.IsSelected"/> in sync with their own
/// <see cref="IsChecked"/>, so the existing purge logic that walks the
/// item list continues to work.
/// </summary>
public class OrphanedNodeViewModel : ViewModelBase
{
    private bool? _isChecked = true;
    private bool _isExpanded;
    private bool _updatingChildren;
    private bool _updatingFromChild;

    public OrphanedNodeViewModel(
        string name,
        string fullPath,
        bool isDirectory,
        OrphanedNodeViewModel? parent = null,
        OrphanedDirectoryItem? item = null)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Parent = parent;
        Item = item;
        Children = [];

        // Default state: matches the wrapped item's IsSelected when present.
        if (item is not null)
            _isChecked = item.IsSelected;

        ToggleExpandCommand = new RelayCommand(_ =>
        {
            if (IsDirectory)
                IsExpanded = !IsExpanded;
        });
    }

    public ICommand ToggleExpandCommand { get; }

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public OrphanedNodeViewModel? Parent { get; internal set; }
    public ObservableCollection<OrphanedNodeViewModel> Children { get; }

    /// <summary>Tree depth, used by the view to indent the name column.</summary>
    public int Depth { get; internal set; }

    /// <summary>
    /// The wrapped item for leaf nodes.  Interior nodes have a null item.
    /// </summary>
    public OrphanedDirectoryItem? Item { get; }

    /// <summary>Number of files at or below this node.</summary>
    public int FileCount { get; set; }

    /// <summary>Total size in bytes of files at or below this node.</summary>
    public long SizeBytes { get; set; }

    public string FormattedSize => FormatBytes(SizeBytes);
    public string FormattedFileCount => FileCount.ToString("N0");

    public bool? IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            OnPropertyChanged();

            // Keep the wrapped item's IsSelected in sync so existing purge
            // code (which iterates items) sees the user's selection.
            if (Item is not null && value.HasValue)
                Item.IsSelected = value.Value;

            if (!_updatingFromChild)
                PropagateToChildren(value);

            if (!_updatingChildren)
                UpdateParentCheck();
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>
    /// Collect all wrapped items at or below this node whose checkbox is
    /// not unchecked.  Used by the purge logic to gather what to delete.
    /// </summary>
    internal IEnumerable<OrphanedDirectoryItem> GetCheckedItems()
    {
        if (IsChecked == false)
            yield break;

        if (Item is not null && IsChecked != false)
            yield return Item;

        foreach (var child in Children)
            foreach (var item in child.GetCheckedItems())
                yield return item;
    }

    /// <summary>
    /// Re-sort this node's children using the given column and direction.
    /// Directories always come before file leaves regardless of sort
    /// direction (matches the source-selection tree's convention).
    /// Recursively sorts every subdirectory too, so the same ordering
    /// applies at every level.
    /// </summary>
    internal void SortChildren(CleanupSortColumn column, bool ascending)
    {
        if (Children.Count == 0) return;

        // Snapshot to a list, sort, then re-add in order.  ObservableCollection
        // doesn't expose Sort, and Move() per item would fire many CollectionChanged
        // events; Clear+Add is one Reset notification which the tree handles fine.
        var sorted = Children.ToList();
        sorted.Sort((a, b) =>
        {
            // Directories before files (always).
            if (a.IsDirectory != b.IsDirectory)
                return a.IsDirectory ? -1 : 1;

            int cmp = column switch
            {
                CleanupSortColumn.Files => a.FileCount.CompareTo(b.FileCount),
                CleanupSortColumn.Size => a.SizeBytes.CompareTo(b.SizeBytes),
                _ => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            };
            if (cmp == 0)
                cmp = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return ascending ? cmp : -cmp;
        });

        Children.Clear();
        foreach (var c in sorted)
            Children.Add(c);

        foreach (var c in Children)
            c.SortChildren(column, ascending);
    }

    // ---------------------------------------------------------------

    private void PropagateToChildren(bool? value)
    {
        if (!IsDirectory || value is null) return;

        _updatingChildren = true;
        try
        {
            foreach (var child in Children)
                child.IsChecked = value;
        }
        finally
        {
            _updatingChildren = false;
        }
    }

    private void UpdateParentCheck()
    {
        if (Parent is null) return;

        Parent._updatingFromChild = true;
        try
        {
            if (Parent.Children.All(c => c.IsChecked == true))
                Parent.IsChecked = true;
            else if (Parent.Children.All(c => c.IsChecked == false))
                Parent.IsChecked = false;
            else
                Parent.IsChecked = null;
        }
        finally
        {
            Parent._updatingFromChild = false;
        }
    }

    // ---------------------------------------------------------------

    // Sizes in the Cleanup view are shown as a raw byte count (no KB/MB/GB).
    private static string FormatBytes(long bytes) => $"{bytes:N0}";
}

/// <summary>
/// One category of orphaned items (e.g. all "Removed from Sources"
/// directories), displayed as a collapsible card with its own tree.
/// </summary>
public class OrphanedCategoryViewModel : ViewModelBase
{
    private bool _isExpanded = true;

    public OrphanedCategoryViewModel(
        OrphanedReason reason,
        ObservableCollection<OrphanedNodeViewModel> rootNodes)
    {
        Reason = reason;
        RootNodes = rootNodes;

        foreach (var node in RootNodes)
            WireCheckNotifications(node);
    }

    public OrphanedReason Reason { get; }
    public ObservableCollection<OrphanedNodeViewModel> RootNodes { get; }

    public string Title => Reason switch
    {
        OrphanedReason.RemovedFromSources => "Not in Source Selection",
        OrphanedReason.DeletedFromDisk => "Deleted from Disk",
        OrphanedReason.MatchesExclusionPattern => "Matches Exclusion Pattern (manual scan)",
        OrphanedReason.MatchesConfiguredExclusion => "Matches Exclusion Filter",
        OrphanedReason.ExcessVersion => "Excess Versions (retention)",
        OrphanedReason.UntrackedFile => "Untracked Files (destination scan)",
        OrphanedReason.CatalogDeleted => "Catalog-Deleted (still on disk)",
        OrphanedReason.CatalogDuplicate => "Catalog Duplicates",
        _ => "Other",
    };

    public string Description => Reason switch
    {
        OrphanedReason.RemovedFromSources =>
            "Catalogued directories whose paths are no longer covered by any source root.",
        OrphanedReason.DeletedFromDisk =>
            "Catalogued directories that no longer exist on disk.",
        OrphanedReason.MatchesExclusionPattern =>
            "Files matching the manual exclusion patterns entered above.",
        OrphanedReason.MatchesConfiguredExclusion =>
            "Files matching the backup set's configured exclusion rules (extensions or zero-tier sets).",
        OrphanedReason.ExcessVersion =>
            "Older versions of files (stored under {drive}_prev) that exceed the configured retention tier limits.",
        OrphanedReason.UntrackedFile =>
            "Files in the destination directory that are not tracked by the catalog (e.g. leftover from a previous backup tool).",
        OrphanedReason.CatalogDeleted =>
            "Files marked as deleted in the catalog but still physically present on disk.",
        OrphanedReason.CatalogDuplicate =>
            "Extra catalog rows pointing at the current copy of a file — usually left over from re-running 'Seed from existing backup' on the same destination.",
        _ => "",
    };

    /// <summary>
    /// One-line statement of what purging this category actually does, so the
    /// user can tell at a glance which sections physically delete backed-up
    /// files from the destination and which only rewrite catalog records.
    /// Shown as a coloured notice under the description in the view.
    /// </summary>
    public string PurgeEffect => Reason switch
    {
        OrphanedReason.CatalogDuplicate =>
            "Purge effect: removes only the duplicate catalog rows \u2014 no files on disk are deleted.",
        OrphanedReason.ExcessVersion =>
            "Purge effect: deletes these older backed-up versions from the destination and marks their catalog rows deleted.",
        OrphanedReason.UntrackedFile =>
            "Purge effect: deletes these files from the destination (they have no catalog record).",
        OrphanedReason.CatalogDeleted =>
            "Purge effect: deletes these files from the destination (their catalog rows are already marked deleted).",
        _ =>
            "Purge effect: deletes these backed-up copies from the destination and marks their catalog rows deleted. Your source files are not touched.",
    };

    /// <summary>
    /// True when purging this category physically deletes files from the
    /// destination.  False only for <see cref="OrphanedReason.CatalogDuplicate"/>,
    /// which rewrites catalog rows and leaves every file on disk alone.
    /// Drives the colour of the purge-effect notice.
    /// </summary>
    public bool PurgeDeletesFiles => Reason != OrphanedReason.CatalogDuplicate;

    public int TotalFileCount => RootNodes.Sum(n => n.FileCount);
    public long TotalSize => RootNodes.Sum(n => n.SizeBytes);
    public string FormattedTotalSize => FormatBytes(TotalSize);
    public string FormattedTotalFileCount => TotalFileCount.ToString("N0");

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public bool? IsAllChecked
    {
        get
        {
            if (RootNodes.Count == 0) return false;
            bool allTrue = RootNodes.All(n => n.IsChecked == true);
            bool allFalse = RootNodes.All(n => n.IsChecked == false);
            if (allTrue) return true;
            if (allFalse) return false;
            return null;
        }
        set
        {
            bool target = value ?? true;
            foreach (var node in RootNodes)
                node.IsChecked = target;
            OnPropertyChanged();
        }
    }

    internal bool HasCheckedItems =>
        RootNodes.Any(n => n.IsChecked != false);

    /// <summary>
    /// Re-sort all root nodes (and their descendants recursively) by the
    /// given column and direction.  Called by the parent ViewModel when the
    /// user clicks a column header.
    /// </summary>
    internal void ApplySort(CleanupSortColumn column, bool ascending)
    {
        var sorted = RootNodes.ToList();
        sorted.Sort((a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory)
                return a.IsDirectory ? -1 : 1;

            int cmp = column switch
            {
                CleanupSortColumn.Files => a.FileCount.CompareTo(b.FileCount),
                CleanupSortColumn.Size => a.SizeBytes.CompareTo(b.SizeBytes),
                _ => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            };
            if (cmp == 0)
                cmp = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return ascending ? cmp : -cmp;
        });

        RootNodes.Clear();
        foreach (var n in sorted)
            RootNodes.Add(n);

        foreach (var n in RootNodes)
            n.SortChildren(column, ascending);
    }

    /// <summary>
    /// Enumerate every wrapped <see cref="OrphanedDirectoryItem"/> in this
    /// category whose corresponding tree node is checked.
    /// </summary>
    internal IEnumerable<OrphanedDirectoryItem> GetCheckedItems()
    {
        foreach (var root in RootNodes)
            foreach (var item in root.GetCheckedItems())
                yield return item;
    }

    private void WireCheckNotifications(OrphanedNodeViewModel node)
    {
        node.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OrphanedNodeViewModel.IsChecked))
                OnPropertyChanged(nameof(IsAllChecked));
        };

        foreach (var child in node.Children)
            WireCheckNotifications(child);
    }

    // Sizes in the Cleanup view are shown as a raw byte count (no KB/MB/GB).
    private static string FormatBytes(long bytes) => $"{bytes:N0}";
}
