namespace LithicBackup.ViewModels;

/// <summary>
/// ViewModel for the per-directory exclusion editor dialog.
/// Displays the directory's own patterns and any patterns inherited from ancestors.
/// Supports two UI modes:
///   - Exclude mode (default): user specifies exclude patterns, optionally with re-include overrides.
///   - Include-only mode: user specifies which files to keep; behind the scenes this sets
///     exclude = "*" and uses IncludedPatterns as the whitelist.
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

        IncludedPatterns = node.IncludedPatterns;

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

        IncludedPatterns = includedPatterns.Count > 0
            ? string.Join("\n", includedPatterns)
            : "";

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

    private string _includedPatterns = "";

    /// <summary>
    /// Re-include / include-only patterns as a newline-separated string.
    /// Edited directly in a multiline TextBox.
    /// </summary>
    public string IncludedPatterns
    {
        get => _includedPatterns;
        set => SetProperty(ref _includedPatterns, value);
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

}
