using System.Collections.ObjectModel;
using LithicBackup.Core.Models;

namespace LithicBackup.ViewModels;

/// <summary>
/// ViewModel for a named version tier set. Wraps a name and a collection
/// of <see cref="RetentionTierViewModel"/> rows for editing in the UI.
/// </summary>
public class TierSetViewModel : ViewModelBase
{
    private string _name;
    private string _filePatternsText = "";
    private string _fileExemptPatternsText = "";

    /// <summary>
    /// Raised when the name changes so the parent can update references.
    /// Args: (oldName, newName).
    /// </summary>
    public event Action<string, string>? NameChanged;

    public TierSetViewModel(string name, bool isBuiltIn)
    {
        _name = name;
        IsBuiltIn = isBuiltIn;
        Tiers = [];
    }

    /// <summary>
    /// Display name (e.g. "Default", "None", "Photos").
    /// </summary>
    public string Name
    {
        get => _name;
        set
        {
            string oldName = _name;
            if (!SetProperty(ref _name, value))
                return;
            NameChanged?.Invoke(oldName, value);
        }
    }

    /// <summary>
    /// Whether this is a built-in tier set ("Default" or "None") that
    /// cannot be deleted. Its tiers can still be edited (except "None"
    /// which has no tiers by design).
    /// </summary>
    public bool IsBuiltIn { get; }

    /// <summary>
    /// Retention tiers for this set. An empty collection means no version
    /// history is kept (the "None" tier set).
    /// </summary>
    public ObservableCollection<RetentionTierViewModel> Tiers { get; }

    /// <summary>
    /// Newline-separated glob patterns for file paths that should use this
    /// tier set.  Not meaningful for the "Default" tier set (fallback).
    /// </summary>
    public string FilePatternsText
    {
        get => _filePatternsText;
        set => SetProperty(ref _filePatternsText, value);
    }

    /// <summary>
    /// Newline-separated glob patterns for file paths exempt from this tier
    /// set, overriding <see cref="FilePatternsText"/>.
    /// </summary>
    public string FileExemptPatternsText
    {
        get => _fileExemptPatternsText;
        set => SetProperty(ref _fileExemptPatternsText, value);
    }

    /// <summary>Convert to a persistence model.</summary>
    public VersionTierSet ToModel()
    {
        return new VersionTierSet
        {
            Name = Name,
            Tiers = Tiers.Select(t => t.ToModel()).ToList(),
            FilePatterns = ParsePatterns(_filePatternsText),
            FileExemptPatterns = ParsePatterns(_fileExemptPatternsText),
        };
    }

    /// <summary>Populate this viewmodel from a persistence model.</summary>
    public static TierSetViewModel FromModel(VersionTierSet model, bool isBuiltIn)
    {
        var vm = new TierSetViewModel(model.Name, isBuiltIn)
        {
            _filePatternsText = FormatPatterns(model.FilePatterns),
            _fileExemptPatternsText = FormatPatterns(model.FileExemptPatterns),
        };
        foreach (var tier in model.Tiers)
        {
            var tierVm = RetentionTierViewModel.FromModel(tier);
            tierVm.RemoveRequested += t => vm.Tiers.Remove(t);
            vm.Tiers.Add(tierVm);
        }
        return vm;
    }

    /// <summary>Parse a newline/comma/semicolon-separated pattern string into a list.</summary>
    private static List<string> ParsePatterns(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return [];
        return input
            .Split(['\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Format a pattern list to a newline-separated display string.</summary>
    private static string FormatPatterns(List<string> patterns)
        => patterns.Count > 0 ? string.Join("\n", patterns) : "";

    public override string ToString() => Name;
}
