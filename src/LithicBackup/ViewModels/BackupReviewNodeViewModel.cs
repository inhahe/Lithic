using System.Collections.ObjectModel;
using System.Windows.Input;

namespace LithicBackup.ViewModels;

/// <summary>Whether a reviewed file is new or a changed version of an existing one.</summary>
public enum ReviewFileStatus
{
    /// <summary>File is not yet in the catalog.</summary>
    New,
    /// <summary>File exists in the catalog but has changed since the last backup.</summary>
    Changed,
}

/// <summary>
/// A single node in the post-scan backup-review treeview.  Unlike
/// <see cref="SourceSelectionNodeViewModel"/> (which lazily enumerates the whole
/// filesystem), this tree is fully populated up-front from the computed backup
/// diff, so sizes and counts reflect <em>only the files that will actually be
/// backed up</em> (the incremental delta), not the entire source.
/// </summary>
public class BackupReviewNodeViewModel : ViewModelBase
{
    private bool? _isSelected = true;
    private bool _isExpanded;
    private bool _suppressPropagation;
    private readonly Action? _onSelectionChanged;

    public BackupReviewNodeViewModel(
        string name, string fullPath, bool isDirectory,
        BackupReviewNodeViewModel? parent,
        Action? onSelectionChanged = null)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        Parent = parent;
        _onSelectionChanged = onSelectionChanged ?? parent?._onSelectionChanged;
        Depth = parent is null ? 0 : parent.Depth + 1;
        Children = [];
        ToggleExpandCommand = new RelayCommand(_ =>
        {
            if (IsDirectory)
                IsExpanded = !IsExpanded;
        });
    }

    /// <summary>Display name (leaf segment of the path).</summary>
    public string Name { get; }

    /// <summary>Absolute path to this file or directory.</summary>
    public string FullPath { get; }

    public bool IsDirectory { get; }
    public BackupReviewNodeViewModel? Parent { get; }
    public ObservableCollection<BackupReviewNodeViewModel> Children { get; }

    /// <summary>Tree depth (0 = root) — drives the indent converter in the view.</summary>
    public int Depth { get; }

    /// <summary>For file nodes: new vs. changed. Null for directories.</summary>
    public ReviewFileStatus? Status { get; set; }

    /// <summary>
    /// Size of this leaf file in bytes.  For directories this is 0; the
    /// directory's displayed size is computed from selected descendants.
    /// </summary>
    public long OwnSizeBytes { get; set; }

    public ICommand ToggleExpandCommand { get; }

    /// <summary>Whether the expander arrow should be shown.</summary>
    public bool HasChildren => Children.Count > 0;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>
    /// Tristate selection. For files: true = will be backed up, false =
    /// excluded (null is unused). For directories: true = every listed child
    /// selected, null = some or all listed children deselected. A directory is
    /// never false — see the setter for why (the tree lists only the delta, so
    /// a fully-unchecked folder would wrongly imply the whole folder is dropped).
    /// </summary>
    public bool? IsSelected
    {
        get => _isSelected;
        set
        {
            // Internal recompute (from UpdateFromChildren): store the value
            // verbatim without re-propagating.
            if (_suppressPropagation)
            {
                if (_isSelected == value)
                    return;
                _isSelected = value;
                OnPropertyChanged();
                return;
            }

            // User-driven toggle (checkbox / Select All / Deselect All). The
            // checkbox is two-state, so an incoming value means "select
            // everything listed under me" (true) or "deselect everything listed
            // under me" (false).
            bool selectAll = value == true;

            if (IsDirectory)
            {
                // Push the definite state down to every listed descendant...
                SetSubtree(selectAll);

                // ...but a directory itself must NEVER display fully unchecked.
                // This review tree lists only the current backup delta, so an
                // "empty" folder checkbox would wrongly imply the entire source
                // folder is being dropped. Show checked only when everything
                // listed is selected; otherwise indeterminate (half-checked) to
                // signal "only the listed files below are affected, not the
                // whole folder".
                bool? shown = selectAll ? true : (bool?)null;
                if (_isSelected != shown)
                {
                    _isSelected = shown;
                    OnPropertyChanged();
                }
            }
            else
            {
                if (_isSelected == selectAll)
                    return;
                _isSelected = selectAll;
                OnPropertyChanged();
            }

            // Propagate up: recompute each ancestor's tristate.
            Parent?.UpdateFromChildren();

            // Sizes/counts changed for this node and every ancestor.
            RefreshSizeSelfAndAncestors();

            // Notify the owning viewmodel so it can refresh totals / fit status.
            _onSelectionChanged?.Invoke();
        }
    }

    /// <summary>Recompute this node's tristate from its children.</summary>
    internal void UpdateFromChildren()
    {
        if (Children.Count == 0)
            return;

        bool allSelected = Children.All(c => c.IsSelected == true);

        _suppressPropagation = true;
        // A directory is checked only when every listed child is selected;
        // otherwise indeterminate. It never reads as fully unchecked (see the
        // IsSelected setter) because the tree lists only the delta, not the
        // folder's full contents — a fully-unchecked folder would misleadingly
        // suggest the whole folder is being removed.
        IsSelected = allSelected ? true : (bool?)null;
        _suppressPropagation = false;

        Parent?.UpdateFromChildren();
    }

    /// <summary>
    /// Recursively force every descendant to a definite selected state,
    /// bypassing the per-node setter so children don't each re-propagate up.
    /// Raises the change notifications needed to refresh each descendant's
    /// checkbox and its size/count columns.
    /// </summary>
    private void SetSubtree(bool selected)
    {
        foreach (var child in Children)
        {
            // Files take the definite state; sub-directories follow the same
            // "never fully unchecked" rule as their parent, so a deselected
            // sub-directory reads as indeterminate rather than empty.
            bool? childState = selected
                ? true
                : (child.IsDirectory ? (bool?)null : false);

            if (child._isSelected != childState)
            {
                child._isSelected = childState;
                child.OnPropertyChanged(nameof(IsSelected));
            }

            // Recurse before notifying so aggregated values reflect descendants.
            child.SetSubtree(selected);

            child.OnPropertyChanged(nameof(SelectedSizeBytes));
            child.OnPropertyChanged(nameof(SelectedFileCount));
            child.OnPropertyChanged(nameof(FormattedSize));
            child.OnPropertyChanged(nameof(FormattedFileCount));
        }
    }

    /// <summary>Sum of the sizes of all <em>selected</em> descendant files.</summary>
    public long SelectedSizeBytes
    {
        get
        {
            if (!IsDirectory)
                return IsSelected == true ? OwnSizeBytes : 0;
            long total = 0;
            foreach (var child in Children)
                total += child.SelectedSizeBytes;
            return total;
        }
    }

    /// <summary>Count of all <em>selected</em> descendant files.</summary>
    public int SelectedFileCount
    {
        get
        {
            if (!IsDirectory)
                return IsSelected == true ? 1 : 0;
            int total = 0;
            foreach (var child in Children)
                total += child.SelectedFileCount;
            return total;
        }
    }

    /// <summary>Selected size in raw bytes with thousands separators (e.g. "1,234,567").</summary>
    public string FormattedSize => FormatBytes(SelectedSizeBytes);

    /// <summary>File-count column text. Empty for individual files.</summary>
    public string FormattedFileCount
        => IsDirectory ? $"{SelectedFileCount:N0}" : string.Empty;

    /// <summary>
    /// Total size in bytes of every delta file under this node, regardless of
    /// selection.  Used as a stable sort key (unlike <see cref="SelectedSizeBytes"/>,
    /// which changes as the user toggles checkboxes).
    /// </summary>
    public long TotalSizeBytes
    {
        get
        {
            if (!IsDirectory)
                return OwnSizeBytes;
            long total = 0;
            foreach (var child in Children)
                total += child.TotalSizeBytes;
            return total;
        }
    }

    /// <summary>Total count of delta files under this node, regardless of selection.</summary>
    public int TotalFileCount
    {
        get
        {
            if (!IsDirectory)
                return 1;
            int total = 0;
            foreach (var child in Children)
                total += child.TotalFileCount;
            return total;
        }
    }

    /// <summary>
    /// Status label for the row.  File rows show their own "New"/"Changed".
    /// Directory rows aggregate their descendants: "New" (all new), "Changed"
    /// (all changed), or "Mixed" (both present).
    /// </summary>
    public string StatusText
    {
        get
        {
            if (!IsDirectory)
                return Status switch
                {
                    ReviewFileStatus.New => "New",
                    ReviewFileStatus.Changed => "Changed",
                    _ => string.Empty,
                };

            var (anyNew, anyChanged) = AggregateStatus();
            if (anyNew && anyChanged) return "Mixed";
            if (anyNew) return "New";
            if (anyChanged) return "Changed";
            return string.Empty;
        }
    }

    /// <summary>
    /// Stable sort key for the Status column: New (0) → Changed (1) →
    /// Mixed (2) → none (3).
    /// </summary>
    public int StatusSortKey => StatusText switch
    {
        "New" => 0,
        "Changed" => 1,
        "Mixed" => 2,
        _ => 3,
    };

    /// <summary>Walk descendants to determine which statuses are present.</summary>
    private (bool anyNew, bool anyChanged) AggregateStatus()
    {
        if (!IsDirectory)
            return (Status == ReviewFileStatus.New, Status == ReviewFileStatus.Changed);

        bool anyNew = false, anyChanged = false;
        foreach (var child in Children)
        {
            var (cn, cc) = child.AggregateStatus();
            anyNew |= cn;
            anyChanged |= cc;
        }
        return (anyNew, anyChanged);
    }

    private void RefreshSizeSelfAndAncestors()
    {
        var node = this;
        while (node is not null)
        {
            node.OnPropertyChanged(nameof(SelectedSizeBytes));
            node.OnPropertyChanged(nameof(SelectedFileCount));
            node.OnPropertyChanged(nameof(FormattedSize));
            node.OnPropertyChanged(nameof(FormattedFileCount));
            node = node.Parent;
        }
    }

    /// <summary>Expand this node and all descendants.</summary>
    public void ExpandAll()
    {
        if (!IsDirectory)
            return;
        IsExpanded = true;
        foreach (var child in Children)
            child.ExpandAll();
    }

    // Raw byte count with thousands separators and no unit suffix, per user
    // preference for the review dialog (e.g. "1,234,567" rather than "1.2 MB").
    internal static string FormatBytes(long bytes) => $"{bytes:N0}";
}
