using System.Windows.Input;
using LithicBackup.Core.Models;

namespace LithicBackup.ViewModels;

/// <summary>
/// ViewModel for a single version retention tier row in the backup configuration UI.
/// </summary>
public class RetentionTierViewModel : ViewModelBase
{
    private string _maxAgeDays = "";
    private string _maxVersions = "";

    /// <summary>Raised when the user clicks the remove button for this tier.</summary>
    public event Action<RetentionTierViewModel>? RemoveRequested;

    public RetentionTierViewModel()
    {
        RemoveCommand = new RelayCommand(_ => RemoveRequested?.Invoke(this));
    }

    /// <summary>
    /// Maximum age in days as a text field. Empty means catch-all ("older than everything above").
    /// </summary>
    public string MaxAgeDays
    {
        get => _maxAgeDays;
        set => SetProperty(ref _maxAgeDays, value);
    }

    /// <summary>
    /// Maximum number of versions to keep as a text field. Empty means unlimited (keep all).
    /// </summary>
    public string MaxVersions
    {
        get => _maxVersions;
        set => SetProperty(ref _maxVersions, value);
    }

    /// <summary>Command to remove this tier from the parent collection.</summary>
    public ICommand RemoveCommand { get; }

    /// <summary>
    /// Convert this view model to a <see cref="VersionRetentionTier"/> model.
    /// Empty MaxAgeDays yields null TimeSpan (catch-all tier).
    /// Empty MaxVersions yields null int (unlimited/keep all).
    /// </summary>
    public VersionRetentionTier ToModel()
    {
        TimeSpan? maxAge = null;
        if (!string.IsNullOrWhiteSpace(MaxAgeDays) &&
            double.TryParse(MaxAgeDays, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double days) && days > 0)
        {
            maxAge = TimeSpan.FromDays(days);
        }

        int? maxVersions = null;
        if (!string.IsNullOrWhiteSpace(MaxVersions) &&
            int.TryParse(MaxVersions, out int versions) && versions > 0)
        {
            maxVersions = versions;
        }

        return new VersionRetentionTier
        {
            MaxAge = maxAge,
            MaxVersions = maxVersions,
        };
    }

    /// <summary>
    /// Create a <see cref="RetentionTierViewModel"/> from a <see cref="VersionRetentionTier"/> model.
    /// </summary>
    public static RetentionTierViewModel FromModel(VersionRetentionTier tier)
    {
        return new RetentionTierViewModel
        {
            MaxAgeDays = tier.MaxAge.HasValue ? tier.MaxAge.Value.TotalDays.ToString() : "",
            MaxVersions = tier.MaxVersions.HasValue ? tier.MaxVersions.Value.ToString() : "",
        };
    }
}
