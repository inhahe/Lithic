using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
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
        Tiers.CollectionChanged += OnTiersChanged;
    }

    /// <summary>
    /// Raised when a tier is added or removed, or a tier's days or versions
    /// change. Its argument says whether the NUMBER of tiers changed, which is
    /// what decides if a set with patterns excludes its files (no tiers) or
    /// versions them.
    /// </summary>
    /// <remarks>
    /// The editor's unsaved-changes flag listened only to this set's own
    /// properties (name and patterns), and these edits live one level down, in
    /// the tier collection and the tier rows. So removing Default's last tier,
    /// adding one, or changing "keep 3" to "keep 1" left Save disabled - and the
    /// close prompt silent, so the edit was simply lost on closing.
    /// </remarks>
    public event Action<bool>? TiersEdited;

    private void OnTiersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (RetentionTierViewModel t in e.OldItems)
                t.PropertyChanged -= OnTierPropertyChanged;
        if (e.NewItems is not null)
            foreach (RetentionTierViewModel t in e.NewItems)
                t.PropertyChanged += OnTierPropertyChanged;
        TiersEdited?.Invoke(true);
    }

    private void OnTierPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RetentionTierViewModel.MaxAgeDays)
                           or nameof(RetentionTierViewModel.MaxVersions))
            TiersEdited?.Invoke(false);
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
