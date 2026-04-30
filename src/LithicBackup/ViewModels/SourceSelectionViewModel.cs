using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;
using LithicBackup.Core.Models;

namespace LithicBackup.ViewModels;

/// <summary>
/// ViewModel for the source selection view. Presents the filesystem as a
/// tristate checkbox treeview rooted at available drives.
/// </summary>
public class SourceSelectionViewModel : ViewModelBase
{
    private bool _hasSelection;
    private bool _updatingAllAutoInclude;
    private bool _updatingAllSelected;
    private bool _updatingAllKeepVersions;

    /// <summary>Fired when the user clicks "Next" with a valid selection.</summary>
    public event Action<List<SourceSelection>>? NextRequested;

    /// <summary>Fired when the user clicks "Cancel".</summary>
    public event Action? CancelRequested;

    public SourceSelectionViewModel()
    {
        Roots = [];
        NextCommand = new RelayCommand(_ => OnNext(), _ => HasSelection);
        CancelCommand = new RelayCommand(_ => CancelRequested?.Invoke());
        LoadDriveRoots();
    }

    public ObservableCollection<SourceSelectionNodeViewModel> Roots { get; }

    public bool HasSelection
    {
        get => _hasSelection;
        set => SetProperty(ref _hasSelection, value);
    }

    /// <summary>
    /// Header checkbox for the Auto-include column.
    /// Setting this propagates to all directory nodes in the tree.
    /// Toggle logic: if all checked → uncheck all, otherwise → check all.
    /// </summary>
    public bool? IsAllAutoIncludeNew
    {
        get
        {
            var dirs = GetAllDirectoryNodes();
            if (dirs.Count == 0) return false;
            bool allTrue = dirs.All(d => d.AutoIncludeNew);
            bool allFalse = dirs.All(d => !d.AutoIncludeNew);
            if (allTrue) return true;
            if (allFalse) return false;
            return null;
        }
        set
        {
            if (_updatingAllAutoInclude) return;
            _updatingAllAutoInclude = true;
            try
            {
                var dirs = GetAllDirectoryNodes();
                if (dirs.Count == 0) return;
                bool target = !dirs.All(d => d.AutoIncludeNew);
                foreach (var node in dirs)
                    node.AutoIncludeNew = target;
                OnPropertyChanged();
            }
            finally
            {
                _updatingAllAutoInclude = false;
            }
        }
    }

    /// <summary>
    /// Header checkbox for the Include column.
    /// Setting this propagates to all root nodes (which cascade to children).
    /// Toggle logic: if all checked → uncheck all, otherwise → check all.
    /// </summary>
    public bool? IsAllSelected
    {
        get
        {
            if (Roots.Count == 0) return false;
            bool allTrue = Roots.All(r => r.IsSelected == true);
            bool allFalse = Roots.All(r => r.IsSelected == false);
            if (allTrue) return true;
            if (allFalse) return false;
            return null;
        }
        set
        {
            if (_updatingAllSelected) return;
            _updatingAllSelected = true;
            try
            {
                bool target = !Roots.All(r => r.IsSelected == true);
                foreach (var root in Roots)
                    root.IsSelected = target;
                OnPropertyChanged();
            }
            finally
            {
                _updatingAllSelected = false;
            }
        }
    }

    /// <summary>
    /// Header checkbox for the Keep Versions column.
    /// Setting this propagates to all nodes in the tree.
    /// Toggle logic: if all checked → uncheck all, otherwise → check all.
    /// </summary>
    public bool? IsAllKeepVersionHistory
    {
        get
        {
            var all = GetAllNodes();
            if (all.Count == 0) return true;
            bool allTrue = all.All(n => n.KeepVersionHistory);
            bool allFalse = all.All(n => !n.KeepVersionHistory);
            if (allTrue) return true;
            if (allFalse) return false;
            return null;
        }
        set
        {
            if (_updatingAllKeepVersions) return;
            _updatingAllKeepVersions = true;
            try
            {
                var all = GetAllNodes();
                if (all.Count == 0) return;
                bool target = !all.All(n => n.KeepVersionHistory);
                foreach (var node in Roots)
                    node.KeepVersionHistory = target; // propagates down
                OnPropertyChanged();
            }
            finally
            {
                _updatingAllKeepVersions = false;
            }
        }
    }

    public ICommand NextCommand { get; }
    public ICommand CancelCommand { get; }

    /// <summary>
    /// Called by the view (or a timer) to recheck whether any files are selected.
    /// </summary>
    public void RefreshHasSelection()
    {
        HasSelection = Roots.Any(r => r.IsSelected != false);
    }

    private void LoadDriveRoots()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (!drive.IsReady)
                continue;

            if (drive.DriveType == DriveType.CDRom)
                continue;

            var node = new SourceSelectionNodeViewModel(drive.RootDirectory.FullName, true, null);
            Roots.Add(node);
        }
    }

    /// <summary>
    /// Build a list of <see cref="SourceSelection"/> models from the current
    /// treeview state (only includes selected/partially-selected nodes).
    /// </summary>
    public List<SourceSelection> GetSelections()
    {
        var result = new List<SourceSelection>();
        foreach (var root in Roots)
        {
            var model = root.ToModel();
            if (model is not null)
                result.Add(model);
        }
        return result;
    }

    private void OnNext()
    {
        var selections = GetSelections();
        if (selections.Count > 0)
            NextRequested?.Invoke(selections);
    }

    private List<SourceSelectionNodeViewModel> GetAllDirectoryNodes()
    {
        var result = new List<SourceSelectionNodeViewModel>();
        CollectDirectories(Roots, result);
        return result;
    }

    private static void CollectDirectories(
        IEnumerable<SourceSelectionNodeViewModel> nodes,
        List<SourceSelectionNodeViewModel> result)
    {
        foreach (var node in nodes)
        {
            if (node.IsDirectory)
            {
                result.Add(node);
                CollectDirectories(node.Children, result);
            }
        }
    }

    private List<SourceSelectionNodeViewModel> GetAllNodes()
    {
        var result = new List<SourceSelectionNodeViewModel>();
        CollectAllNodes(Roots, result);
        return result;
    }

    private static void CollectAllNodes(
        IEnumerable<SourceSelectionNodeViewModel> nodes,
        List<SourceSelectionNodeViewModel> result)
    {
        foreach (var node in nodes)
        {
            result.Add(node);
            CollectAllNodes(node.Children, result);
        }
    }
}
