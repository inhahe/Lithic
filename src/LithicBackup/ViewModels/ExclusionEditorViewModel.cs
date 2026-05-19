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
        VersionExcludedPatterns = node.VersionExcludedPatterns;
        VersionIncludedPatterns = node.VersionIncludedPatterns;

        // Collect inherited exclusions from parent directories.
        var inherited = new List<string>();
        var inheritedVersion = new List<string>();
        var current = node.Parent;
        while (current is not null)
        {
            string label = string.IsNullOrEmpty(current.Path) ? "All Drives" : current.Name;
            if (!string.IsNullOrWhiteSpace(current.ExcludedPatterns))
            {
                foreach (var line in current.ExcludedPatterns.Split(['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    inherited.Add($"{line}  ({label})");
                }
            }
            if (!string.IsNullOrWhiteSpace(current.VersionExcludedPatterns))
            {
                foreach (var line in current.VersionExcludedPatterns.Split(['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    inheritedVersion.Add($"{line}  ({label})");
                }
            }
            current = current.Parent;
        }

        InheritedExclusionsText = inherited.Count > 0
            ? string.Join("\n", inherited)
            : "";
        HasInheritedExclusions = inherited.Count > 0;

        InheritedVersionExclusionsText = inheritedVersion.Count > 0
            ? string.Join("\n", inheritedVersion)
            : "";
        HasInheritedVersionExclusions = inheritedVersion.Count > 0;

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
        string inheritedExclusionsText,
        List<string>? versionExcludedPatterns = null,
        List<string>? versionIncludedPatterns = null,
        string? inheritedVersionExclusionsText = null)
    {
        DirectoryName = directoryName;
        DirectoryPath = directoryPath;
        ExcludedPatterns = excludedPatterns.Count > 0 ? string.Join("\n", excludedPatterns) : "";
        InheritedExclusionsText = inheritedExclusionsText;
        HasInheritedExclusions = !string.IsNullOrEmpty(inheritedExclusionsText);

        InheritedVersionExclusionsText = inheritedVersionExclusionsText ?? "";
        HasInheritedVersionExclusions = !string.IsNullOrEmpty(inheritedVersionExclusionsText);

        IncludedPatterns = includedPatterns.Count > 0
            ? string.Join("\n", includedPatterns)
            : "";

        VersionExcludedPatterns = versionExcludedPatterns is { Count: > 0 }
            ? string.Join("\n", versionExcludedPatterns)
            : "";
        VersionIncludedPatterns = versionIncludedPatterns is { Count: > 0 }
            ? string.Join("\n", versionIncludedPatterns)
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
    public string InheritedVersionExclusionsText { get; }
    public bool HasInheritedVersionExclusions { get; }

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

    private string _versionExcludedPatterns = "";

    /// <summary>
    /// Glob patterns for files whose past versions should not be retained.
    /// Separate from the backup exclusion patterns — these files are still
    /// backed up, but old versions are deleted during retention cleanup.
    /// </summary>
    public string VersionExcludedPatterns
    {
        get => _versionExcludedPatterns;
        set => SetProperty(ref _versionExcludedPatterns, value);
    }

    private string _versionIncludedPatterns = "";

    /// <summary>
    /// Patterns to override version exclusions inherited from parent directories,
    /// re-enabling version retention for matching files.
    /// </summary>
    public string VersionIncludedPatterns
    {
        get => _versionIncludedPatterns;
        set => SetProperty(ref _versionIncludedPatterns, value);
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
