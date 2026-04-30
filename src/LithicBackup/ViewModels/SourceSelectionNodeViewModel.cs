using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;

namespace LithicBackup.ViewModels;

/// <summary>
/// ViewModel for a single node in the source selection treeview.
/// Implements tristate checkbox logic: parent propagates to children,
/// children propagate up to parent.
/// </summary>
public class SourceSelectionNodeViewModel : ViewModelBase
{
    private bool? _isSelected = false;
    private bool _autoIncludeNew = true;
    private bool _keepVersionHistory = true;
    private bool _isExpanded;
    private bool _isLoaded;
    private bool _suppressPropagation;

    public SourceSelectionNodeViewModel(string path, bool isDirectory, SourceSelectionNodeViewModel? parent)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        if (string.IsNullOrEmpty(Name))
            Name = path; // Drive root (e.g. "C:\")
        IsDirectory = isDirectory;
        Parent = parent;
        Depth = parent is null ? 0 : parent.Depth + 1;
        Children = [];
        ToggleExpandCommand = new RelayCommand(_ =>
        {
            if (IsDirectory)
                IsExpanded = !IsExpanded;
        });

        // Directories get a dummy child so the expander arrow shows.
        if (isDirectory && !_isLoaded)
            Children.Add(new SourceSelectionNodeViewModel("Loading...", false, this) { _isSelected = false });
    }

    public string Path { get; }
    public string Name { get; }
    public bool IsDirectory { get; }
    public SourceSelectionNodeViewModel? Parent { get; }
    public ObservableCollection<SourceSelectionNodeViewModel> Children { get; }

    /// <summary>Single-click on the name area toggles expand/collapse for directories.</summary>
    public ICommand ToggleExpandCommand { get; }

    /// <summary>Nesting depth (0 for root nodes). Used for indentation in the custom TreeViewItem template.</summary>
    public int Depth { get; }

    /// <summary>
    /// Tristate: true = included, false = excluded, null = partially included.
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

            // Propagate down: set all children to the same definite state.
            if (value.HasValue && IsDirectory && _isLoaded)
            {
                foreach (var child in Children)
                {
                    child._suppressPropagation = true;
                    child.IsSelected = value;
                    child._suppressPropagation = false;
                }
            }

            // Propagate up: recalculate parent's tristate.
            Parent?.UpdateFromChildren();
        }
    }

    /// <summary>
    /// Whether new subdirectories added in the future should be automatically included.
    /// Only meaningful for directories.
    /// </summary>
    public bool AutoIncludeNew
    {
        get => _autoIncludeNew;
        set
        {
            if (!SetProperty(ref _autoIncludeNew, value))
                return;

            // Propagate down to all loaded child directories.
            if (IsDirectory && _isLoaded)
            {
                foreach (var child in Children)
                {
                    if (child.IsDirectory)
                        child.AutoIncludeNew = value;
                }
            }
        }
    }

    /// <summary>
    /// Whether to keep previous versions when files change. When false,
    /// changed files are overwritten without preserving history.
    /// Propagates down to all children.
    /// </summary>
    public bool KeepVersionHistory
    {
        get => _keepVersionHistory;
        set
        {
            if (!SetProperty(ref _keepVersionHistory, value))
                return;

            // Propagate down to all loaded children.
            if (IsDirectory && _isLoaded)
            {
                foreach (var child in Children)
                    child.KeepVersionHistory = value;
            }
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value) && value && !_isLoaded && IsDirectory)
                _ = LoadChildrenAsync();
        }
    }

    /// <summary>
    /// Lazy-load children from the filesystem when the node is first expanded.
    /// Runs the filesystem enumeration on a background thread so the UI stays
    /// responsive and the "Loading..." placeholder remains visible.
    /// </summary>
    private async Task LoadChildrenAsync()
    {
        _isLoaded = true;

        // Yield at a priority below Render so the WPF layout/render pass
        // completes first — this ensures the "Loading..." placeholder is
        // painted before the filesystem enumeration begins.
        await System.Windows.Threading.Dispatcher.Yield(
            System.Windows.Threading.DispatcherPriority.Background);

        try
        {
            var entries = await Task.Run(() =>
            {
                var result = new List<(string FullName, bool IsDirectory)>();
                var dirInfo = new DirectoryInfo(Path);

                try
                {
                    foreach (var subDir in dirInfo.EnumerateDirectories())
                    {
                        try
                        {
                            if ((subDir.Attributes & (FileAttributes.System | FileAttributes.Hidden)) != 0)
                                continue;
                            result.Add((subDir.FullName, true));
                        }
                        catch (UnauthorizedAccessException) { }
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }

                try
                {
                    foreach (var file in dirInfo.EnumerateFiles())
                    {
                        try
                        {
                            result.Add((file.FullName, false));
                        }
                        catch (UnauthorizedAccessException) { }
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }

                return result;
            });

            // Back on the UI thread — swap the placeholder for real children.
            Children.Clear();

            foreach (var (fullName, isDir) in entries)
            {
                var child = new SourceSelectionNodeViewModel(fullName, isDir, this)
                {
                    _isSelected = _isSelected ?? false,
                    _autoIncludeNew = isDir ? _autoIncludeNew : true,
                    _keepVersionHistory = _keepVersionHistory,
                };
                Children.Add(child);
            }
        }
        catch (Exception)
        {
            // Remove the "Loading..." placeholder on error so it doesn't linger.
            Children.Clear();
        }
    }

    /// <summary>
    /// Recalculate this node's tristate based on its children's states.
    /// </summary>
    private void UpdateFromChildren()
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
            IsSelected = null; // Mixed — tristate indeterminate

        _suppressPropagation = false;

        // Continue propagating up.
        Parent?.UpdateFromChildren();
    }

    /// <summary>
    /// Build a <see cref="Core.Models.SourceSelection"/> tree from this viewmodel tree.
    /// Only includes nodes that are at least partially selected.
    /// </summary>
    public Core.Models.SourceSelection? ToModel()
    {
        if (IsSelected == false)
            return null;

        var model = new Core.Models.SourceSelection
        {
            Path = Path,
            IsDirectory = IsDirectory,
            IsSelected = IsSelected,
            AutoIncludeNewSubdirectories = AutoIncludeNew,
            KeepVersionHistory = KeepVersionHistory,
        };

        if (IsDirectory && _isLoaded)
        {
            foreach (var child in Children)
            {
                var childModel = child.ToModel();
                if (childModel is not null)
                    model.Children.Add(childModel);
            }
        }

        return model;
    }
}
