using System.Windows.Input;
using System.Windows.Threading;
using LithicBackup.Core.Models;

namespace LithicBackup.ViewModels;

/// <summary>
/// A node in the restore file tree: either a directory or a leaf file. Unlike the
/// old eager tree (which materialised every catalog record up front), this node
/// loads a directory's <em>direct</em> children only when it is first expanded,
/// via a catalog skip-scan (<see cref="RestoreTreeChild"/>). The whole set is
/// therefore never read to browse it, and only the selected subset is read at
/// restore time.
/// <para>
/// Tristate checkboxes work the same as the source-selection tree, but exploit a
/// key invariant that makes laziness tractable: an <em>unexpanded</em> directory
/// is always fully checked or fully unchecked (never indeterminate). It only
/// becomes indeterminate once its children are loaded and the user deselects some
/// of them. So a definite (true/false) directory state uniformly covers its whole
/// subtree — descendants inherit it when they lazily load — and a fully-checked
/// directory can be restored by a single prefix query without enumerating it in
/// the UI.
/// </para>
/// </summary>
public class RestoreNodeViewModel : ViewModelBase
{
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<RestoreTreeChild>>>? _loadChildren;
    private readonly Action? _onSelectionChanged;
    private bool? _isSelected = false;
    private bool _isExpanded;
    private bool _isLoaded;
    private bool _suppressPropagation;
    private Task? _loadTask;

    /// <summary>Create a real (directory or file) node.</summary>
    public RestoreNodeViewModel(
        string name,
        string fullPath,
        int depth,
        bool isDirectory,
        long sizeBytes,
        DateTime? backedUpUtc,
        Func<string, CancellationToken, Task<IReadOnlyList<RestoreTreeChild>>>? loadChildren,
        Action? onSelectionChanged)
    {
        Name = name;
        FullPath = fullPath;
        Depth = depth;
        IsDirectory = isDirectory;
        SizeBytes = sizeBytes;
        BackedUpUtc = backedUpUtc;
        _loadChildren = loadChildren;
        _onSelectionChanged = onSelectionChanged;
        Children = [];
        ToggleExpandCommand = new RelayCommand(_ =>
        {
            if (IsDirectory)
                IsExpanded = !IsExpanded;
        });

        // Directories get a dummy "Loading..." child so the expander arrow shows
        // before the real children have been fetched (HasItems drives the arrow).
        if (isDirectory)
            Children.Add(CreatePlaceholder(this));
    }

    private RestoreNodeViewModel(RestoreNodeViewModel parent)
    {
        // Placeholder-only ctor.
        Name = "Loading...";
        FullPath = "";
        Depth = parent.Depth + 1;
        IsDirectory = false;
        IsPlaceholder = true;
        Parent = parent;
        Children = [];
        ToggleExpandCommand = new RelayCommand(_ => { });
    }

    private static RestoreNodeViewModel CreatePlaceholder(RestoreNodeViewModel parent) => new(parent);

    public string Name { get; }

    /// <summary>
    /// Full source path. For a directory this is the prefix (no trailing
    /// separator, e.g. <c>D:\docs</c>) used to list its children and to restore
    /// its whole subtree; for a file it is the file's own source path.
    /// </summary>
    public string FullPath { get; }

    public int Depth { get; }
    public bool IsDirectory { get; }

    /// <summary>True for the transient "Loading..." row; never a real selectable item.</summary>
    public bool IsPlaceholder { get; }

    /// <summary>File size in bytes; 0 for directories (not aggregated in lazy mode).</summary>
    public long SizeBytes { get; }

    /// <summary>Human-readable size for the Size column; blank for directories.</summary>
    public string SizeText => IsDirectory || IsPlaceholder ? "" : $"{SizeBytes:N0}";

    /// <summary>Backup timestamp for leaf files; null for directories.</summary>
    public DateTime? BackedUpUtc { get; }

    public BulkObservableCollection<RestoreNodeViewModel> Children { get; }
    public RestoreNodeViewModel? Parent { get; set; }

    public ICommand ToggleExpandCommand { get; }

    /// <summary>Whether this directory's children have been loaded from the catalog.</summary>
    public bool IsLoaded => _isLoaded;

    /// <summary>
    /// Tristate selection: true = included, false = excluded, null = partial
    /// (only reachable once children are loaded). Setting a definite value
    /// propagates down to loaded children and recomputes ancestors; an unloaded
    /// directory just stores the definite state, which its children inherit when
    /// they load.
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

            // Propagate down into loaded children only. Unloaded directories have
            // nothing to push to yet — their (definite) state is inherited when
            // the children are lazily materialised.
            if (value.HasValue && IsDirectory && _isLoaded)
            {
                foreach (var child in Children)
                    child.SetSelectedRecursive(value.Value);
            }

            Parent?.UpdateFromChildren();
            _onSelectionChanged?.Invoke();
        }
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value) || !value || !IsDirectory)
                return;

            // First expansion — fetch this directory's direct children. The
            // catalog is a static snapshot, so (unlike the backup-source tree) we
            // never re-read on subsequent expansions.
            if (!_isLoaded)
                _loadTask = LoadChildrenAsync();
        }
    }

    /// <summary>
    /// Ensure this directory's children are loaded (used by programmatic expansion
    /// paths that need the children present before acting on them).
    /// </summary>
    internal async Task EnsureChildrenLoadedAsync()
    {
        if (!IsDirectory)
            return;
        if (!_isLoaded)
            _loadTask = LoadChildrenAsync();
        if (_loadTask is not null)
            await _loadTask;
    }

    private async Task LoadChildrenAsync()
    {
        _isLoaded = true;
        if (_loadChildren is null)
            return;

        // Yield below Render so the "Loading..." placeholder paints before the
        // catalog query begins.
        await Dispatcher.Yield(DispatcherPriority.Background);

        try
        {
            var children = await _loadChildren(FullPath, CancellationToken.None);

            // Before expansion this node's state is always definite (the lazy
            // invariant), so freshly-materialised children uniformly inherit it.
            bool inheritTrue = _isSelected == true;

            var nodes = new List<RestoreNodeViewModel>(children.Count);
            foreach (var child in children)
            {
                var node = new RestoreNodeViewModel(
                    child.Name, child.FullPath, Depth + 1, child.IsDirectory,
                    child.SizeBytes, child.BackedUpUtc, _loadChildren, _onSelectionChanged)
                {
                    Parent = this,
                };
                if (inheritTrue)
                    node.InitSelectedTrue();
                nodes.Add(node);
            }

            // Single Reset notification instead of N Adds (avoids TreeView layout storms).
            Children.ReplaceAll(nodes);

            // If this directory was fully checked, its just-loaded descendants are
            // now checked too — let the owner recount the selection.
            if (inheritTrue)
                _onSelectionChanged?.Invoke();
        }
        catch
        {
            // Leave the node reloadable on a later expansion rather than committing
            // an empty child list.
            _isLoaded = false;
        }
    }

    /// <summary>Set the initial (pre-child) definite state without propagation.</summary>
    private void InitSelectedTrue()
    {
        _isSelected = true;
        OnPropertyChanged(nameof(IsSelected));
        // Any placeholder child this directory carries also starts checked so the
        // arrow-expand shows a consistent state until real children replace it.
        foreach (var child in Children)
            child.InitSelectedTrue();
    }

    /// <summary>
    /// Set this node and every <em>loaded</em> descendant to a definite state,
    /// notifying each. Unloaded directories keep the definite state for their
    /// children to inherit on load; this neither bubbles up nor fires the
    /// selection callback (the originating top-level setter handles those once).
    /// </summary>
    private void SetSelectedRecursive(bool value)
    {
        if (_isSelected != value)
        {
            _isSelected = value;
            OnPropertyChanged(nameof(IsSelected));
        }

        if (IsDirectory && _isLoaded)
        {
            foreach (var child in Children)
                child.SetSelectedRecursive(value);
        }
    }

    /// <summary>Recompute this node's tristate from its loaded children, then bubble upward.</summary>
    internal void UpdateFromChildren()
    {
        if (!_isLoaded || Children.Count == 0)
            return;

        bool allSelected = Children.All(c => c.IsSelected == true);
        bool allDeselected = Children.All(c => c.IsSelected == false);

        _suppressPropagation = true;
        IsSelected = allSelected ? true : (allDeselected ? false : (bool?)null);
        _suppressPropagation = false;

        Parent?.UpdateFromChildren();
    }

    /// <summary>
    /// Collect this node's contribution to a restore selection: fully-checked
    /// directories add their prefix (their whole subtree is read at restore time
    /// via one prefix query), indeterminate directories recurse into their loaded
    /// children, and checked file leaves add their exact path.
    /// </summary>
    public void CollectSelection(List<string> dirPrefixes, List<string> filePaths)
    {
        if (IsPlaceholder)
            return;

        if (!IsDirectory)
        {
            if (_isSelected == true)
                filePaths.Add(FullPath);
            return;
        }

        if (_isSelected == true)
        {
            dirPrefixes.Add(FullPath);   // whole subtree included
            return;
        }
        if (_isSelected == false)
            return;

        // Indeterminate ⇒ this directory is loaded (invariant); recurse.
        foreach (var child in Children)
            child.CollectSelection(dirPrefixes, filePaths);
    }
}
