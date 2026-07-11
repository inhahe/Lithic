using System.Collections.ObjectModel;
using System.Windows.Input;
using LithicBackup.Core.Interfaces;

namespace LithicBackup.ViewModels;

/// <summary>
/// A node in the restore file tree: either a directory (with children) or a
/// leaf file (wrapping a <see cref="RestorableFile"/>). Mirrors the tristate
/// checkbox behaviour of the source-selection tree, but the whole tree is
/// built up front from the catalog's flat file list (no lazy loading).
/// </summary>
public class RestoreNodeViewModel : ViewModelBase
{
    private readonly Action? _onSelectionChanged;
    private bool? _isSelected = false;
    private bool _isExpanded;
    private bool _suppressPropagation;

    /// <summary>Create a directory node.</summary>
    public RestoreNodeViewModel(string name, string fullPath, int depth, Action? onSelectionChanged)
    {
        Name = name;
        FullPath = fullPath;
        Depth = depth;
        IsDirectory = true;
        _onSelectionChanged = onSelectionChanged;
        Children = [];
        ToggleExpandCommand = new RelayCommand(_ =>
        {
            if (IsDirectory)
                IsExpanded = !IsExpanded;
        });
    }

    /// <summary>Create a leaf file node.</summary>
    public RestoreNodeViewModel(string name, RestorableFile file, int depth, Action? onSelectionChanged)
    {
        Name = name;
        FullPath = file.Record.SourcePath;
        Depth = depth;
        IsDirectory = false;
        File = file;
        SizeBytes = file.Record.SizeBytes;
        BackedUpUtc = file.Record.BackedUpUtc;
        _onSelectionChanged = onSelectionChanged;
        Children = [];
        ToggleExpandCommand = new RelayCommand(_ => { });
    }

    public string Name { get; }
    public string FullPath { get; }
    public int Depth { get; }
    public bool IsDirectory { get; }

    /// <summary>The restorable file, for leaf nodes; null for directories.</summary>
    public RestorableFile? File { get; }

    /// <summary>File size (leaf) or aggregate of all descendant files (directory).</summary>
    public long SizeBytes { get; set; }

    /// <summary>Backup timestamp for leaf files; null for directories.</summary>
    public DateTime? BackedUpUtc { get; }

    public ObservableCollection<RestoreNodeViewModel> Children { get; }
    public RestoreNodeViewModel? Parent { get; set; }

    public ICommand ToggleExpandCommand { get; }

    /// <summary>
    /// Tristate selection: true = included, false = excluded, null = partial
    /// (some descendants included). Setting it propagates down to children and
    /// recomputes ancestors.
    /// </summary>
    public bool? IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
                return;

            _isSelected = value;
            OnPropertyChanged();

            if (_suppressPropagation)
                return;

            // Propagate down recursively: set every descendant to the same
            // definite state. The tree is built fully up front (no lazy load),
            // so a one-level set would leave deep leaves unchecked and they'd
            // never be collected for restore.
            if (value.HasValue && IsDirectory)
            {
                foreach (var child in Children)
                    child.SetSelectedRecursive(value.Value);
            }

            // Propagate up: recompute the parent's tristate from its children.
            Parent?.UpdateFromChildren();

            _onSelectionChanged?.Invoke();
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    /// <summary>
    /// Set this node and every descendant to a definite state, notifying each,
    /// without bubbling the change back up or firing the selection callback
    /// (the originating top-level setter handles those once).
    /// </summary>
    private void SetSelectedRecursive(bool value)
    {
        if (_isSelected != value)
        {
            _isSelected = value;
            OnPropertyChanged(nameof(IsSelected));
        }

        if (IsDirectory)
        {
            foreach (var child in Children)
                child.SetSelectedRecursive(value);
        }
    }

    /// <summary>
    /// Recompute this node's tristate from its children, then bubble upward.
    /// </summary>
    internal void UpdateFromChildren()
    {
        if (Children.Count == 0)
            return;

        bool allSelected = Children.All(c => c.IsSelected == true);
        bool allDeselected = Children.All(c => c.IsSelected == false);

        _suppressPropagation = true;
        if (allSelected)
            IsSelected = true;
        else if (allDeselected)
            IsSelected = false;
        else
            IsSelected = null;
        _suppressPropagation = false;

        Parent?.UpdateFromChildren();
    }

    /// <summary>
    /// Append every selected leaf file at or below this node to <paramref name="into"/>.
    /// </summary>
    public void CollectSelectedFiles(List<RestorableFile> into)
    {
        if (!IsDirectory)
        {
            if (IsSelected == true && File is not null)
                into.Add(File);
            return;
        }

        foreach (var child in Children)
            child.CollectSelectedFiles(into);
    }
}
