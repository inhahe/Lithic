using System.Collections.ObjectModel;
using System.Windows.Input;

namespace LithicBackup.ViewModels;

/// <summary>
/// A single include/re-include pattern with a per-pattern version-history toggle.
/// </summary>
public class PatternItem : ViewModelBase
{
    private string _pattern = "";
    private bool _keepVersions = true;

    public string Pattern
    {
        get => _pattern;
        set => SetProperty(ref _pattern, value);
    }

    public bool KeepVersions
    {
        get => _keepVersions;
        set => SetProperty(ref _keepVersions, value);
    }
}

/// <summary>
/// ViewModel for the per-directory exclusion editor dialog.
/// Displays the directory's own patterns and any patterns inherited from ancestors.
/// Supports two UI modes:
///   - Exclude mode (default): user specifies exclude patterns, optionally with re-include overrides.
///   - Include-only mode: user specifies which files to keep; behind the scenes this sets
///     exclude = "*" and uses IncludedPatterns as the whitelist.
///
/// Include/re-include patterns each carry a "Keep versions" flag. Patterns whose
/// flag is unchecked are stored with a <c>~nv:</c> prefix in the serialised pattern
/// list so the backup engine can disable version history for matching files.
/// </summary>
public class ExclusionEditorViewModel : ViewModelBase
{
    public ExclusionEditorViewModel(SourceSelectionNodeViewModel node)
    {
        DirectoryName = node.Name;
        DirectoryPath = node.Path;
        ExcludedPatterns = node.ExcludedPatterns;

        // Collect inherited exclusions from parent directories.
        var inherited = new List<string>();
        var current = node.Parent;
        while (current is not null)
        {
            if (!string.IsNullOrWhiteSpace(current.ExcludedPatterns))
            {
                string label = string.IsNullOrEmpty(current.Path) ? "All Drives" : current.Name;
                foreach (var line in current.ExcludedPatterns.Split(['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    inherited.Add($"{line}  ({label})");
                }
            }
            current = current.Parent;
        }

        InheritedExclusionsText = inherited.Count > 0
            ? string.Join("\n", inherited)
            : "";
        HasInheritedExclusions = inherited.Count > 0;

        ParseIncludePatterns(node.IncludedPatterns);

        AddPatternCommand = new RelayCommand(_ => IncludePatternItems.Add(new PatternItem()));
        RemovePatternCommand = new RelayCommand(p =>
        {
            if (p is PatternItem item)
                IncludePatternItems.Remove(item);
        });

        DetectMode();
    }

    /// <summary>
    /// Simplified constructor for editing exclusions from a model node
    /// (used when no <see cref="SourceSelectionNodeViewModel"/> is available).
    /// </summary>
    public ExclusionEditorViewModel(
        string directoryName, string directoryPath,
        List<string> excludedPatterns, List<string> includedPatterns,
        string inheritedExclusionsText)
    {
        DirectoryName = directoryName;
        DirectoryPath = directoryPath;
        ExcludedPatterns = excludedPatterns.Count > 0 ? string.Join("\n", excludedPatterns) : "";
        InheritedExclusionsText = inheritedExclusionsText;
        HasInheritedExclusions = !string.IsNullOrEmpty(inheritedExclusionsText);

        ParseIncludePatterns(includedPatterns.Count > 0
            ? string.Join("\n", includedPatterns)
            : "");

        AddPatternCommand = new RelayCommand(_ => IncludePatternItems.Add(new PatternItem()));
        RemovePatternCommand = new RelayCommand(p =>
        {
            if (p is PatternItem item)
                IncludePatternItems.Remove(item);
        });

        DetectMode();
    }

    /// <summary>
    /// If the only exclusion pattern is "*", the user previously chose include-only mode.
    /// </summary>
    private void DetectMode()
    {
        if (_excludedPatterns.Trim() == "*")
        {
            _isExcludeMode = false;
            _isIncludeOnlyMode = true;
        }
    }

    // ---------------------------------------------------------------
    // Properties
    // ---------------------------------------------------------------

    public string DirectoryName { get; }
    public string DirectoryPath { get; }
    public string InheritedExclusionsText { get; }
    public bool HasInheritedExclusions { get; }

    private string _excludedPatterns = "";
    public string ExcludedPatterns
    {
        get => _excludedPatterns;
        set => SetProperty(ref _excludedPatterns, value);
    }

    /// <summary>
    /// Include/re-include patterns as structured items with per-pattern
    /// "Keep versions" toggle. The UI binds to this collection.
    /// </summary>
    public ObservableCollection<PatternItem> IncludePatternItems { get; } = [];

    /// <summary>
    /// Serialised form of <see cref="IncludePatternItems"/>.
    /// Patterns with <c>KeepVersions == false</c> are prefixed with <c>~nv:</c>.
    /// Read by callers after the dialog closes.
    /// </summary>
    public string IncludedPatterns
    {
        get
        {
            var lines = IncludePatternItems
                .Where(p => !string.IsNullOrWhiteSpace(p.Pattern))
                .Select(p => p.KeepVersions ? p.Pattern.Trim() : $"~nv:{p.Pattern.Trim()}");
            return string.Join("\n", lines);
        }
    }

    // ---------------------------------------------------------------
    // Mode toggle
    // ---------------------------------------------------------------

    private bool _isExcludeMode = true;
    public bool IsExcludeMode
    {
        get => _isExcludeMode;
        set
        {
            if (SetProperty(ref _isExcludeMode, value) && value)
            {
                // Switching to exclude mode: remove the catch-all "*" exclusion
                // that include-only mode set.
                if (ExcludedPatterns.Trim() == "*")
                    ExcludedPatterns = "";
                _isIncludeOnlyMode = false;
                OnPropertyChanged(nameof(IsIncludeOnlyMode));
            }
        }
    }

    private bool _isIncludeOnlyMode;
    public bool IsIncludeOnlyMode
    {
        get => _isIncludeOnlyMode;
        set
        {
            if (SetProperty(ref _isIncludeOnlyMode, value) && value)
            {
                // Switching to include-only mode: exclude everything,
                // then IncludedPatterns serves as the whitelist.
                ExcludedPatterns = "*";
                _isExcludeMode = false;
                OnPropertyChanged(nameof(IsExcludeMode));
            }
        }
    }

    // ---------------------------------------------------------------
    // Commands
    // ---------------------------------------------------------------

    public ICommand AddPatternCommand { get; }
    public ICommand RemovePatternCommand { get; }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    /// <summary>
    /// Parse a newline-separated pattern string into <see cref="IncludePatternItems"/>.
    /// Lines prefixed with <c>~nv:</c> have <c>KeepVersions = false</c>.
    /// </summary>
    private void ParseIncludePatterns(string text)
    {
        IncludePatternItems.Clear();
        if (string.IsNullOrWhiteSpace(text))
            return;

        foreach (var line in text.Split(['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith("~nv:"))
                IncludePatternItems.Add(new PatternItem { Pattern = line[4..], KeepVersions = false });
            else
                IncludePatternItems.Add(new PatternItem { Pattern = line, KeepVersions = true });
        }
    }
}
