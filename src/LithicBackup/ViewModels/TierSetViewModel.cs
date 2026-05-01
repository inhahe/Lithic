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

    /// <summary>Convert to a persistence model.</summary>
    public VersionTierSet ToModel()
    {
        return new VersionTierSet
        {
            Name = Name,
            Tiers = Tiers.Select(t => t.ToModel()).ToList(),
        };
    }

    public override string ToString() => Name;
}
