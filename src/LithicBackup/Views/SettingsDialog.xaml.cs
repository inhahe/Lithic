using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using LithicBackup.Core.Models;
using LithicBackup.Services;

namespace LithicBackup.Views;

/// <summary>
/// Editor for machine-global <see cref="UserSettings"/>. Edits an in-memory
/// copy and writes it back to the supplied instance (and disk) only on Save.
/// </summary>
public partial class SettingsDialog : Window, INotifyPropertyChanged
{
    private readonly UserSettings _settings;

    public SettingsDialog(UserSettings settings)
    {
        _settings = settings;
        InitializeComponent();
        DataContext = this;

        // Seed editable fields from the persisted settings.
        var mb = settings.MemoryBudget ?? new MemoryBudgetOptions();
        _isAuto = mb.Mode == MemoryBudgetMode.Auto;
        _percentOfTotal = mb.PercentOfTotal.ToString(CultureInfo.CurrentCulture);
        _reserveGb = mb.ReserveGb.ToString(CultureInfo.CurrentCulture);
        _fixedGb = mb.FixedGb.ToString(CultureInfo.CurrentCulture);
        _suppressBackupSuggestions = settings.SuppressBackupSuggestions;
    }

    private bool _isAuto;
    public bool IsAuto
    {
        get => _isAuto;
        set
        {
            if (_isAuto == value) return;
            _isAuto = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsFixed));
            RefreshPreview();
        }
    }

    public bool IsFixed
    {
        get => !_isAuto;
        set => IsAuto = !value;
    }

    private string _percentOfTotal = "50";
    public string PercentOfTotal
    {
        get => _percentOfTotal;
        set { _percentOfTotal = value; OnPropertyChanged(); RefreshPreview(); }
    }

    private string _reserveGb = "2";
    public string ReserveGb
    {
        get => _reserveGb;
        set { _reserveGb = value; OnPropertyChanged(); RefreshPreview(); }
    }

    private string _fixedGb = "1";
    public string FixedGb
    {
        get => _fixedGb;
        set { _fixedGb = value; OnPropertyChanged(); RefreshPreview(); }
    }

    private bool _suppressBackupSuggestions;
    public bool SuppressBackupSuggestions
    {
        get => _suppressBackupSuggestions;
        set { _suppressBackupSuggestions = value; OnPropertyChanged(); }
    }

    private string _systemMemoryText = "";
    public string SystemMemoryText
    {
        get => _systemMemoryText;
        private set { _systemMemoryText = value; OnPropertyChanged(); }
    }

    private string _computedBudgetText = "";
    public string ComputedBudgetText
    {
        get => _computedBudgetText;
        private set { _computedBudgetText = value; OnPropertyChanged(); }
    }

    private const double GiB = 1024d * 1024d * 1024d;

    private void RefreshPreview()
    {
        var (total, available) = MemoryBudget.GetSystemMemory();
        if (total > 0)
        {
            SystemMemoryText =
                $"System memory: {total / GiB:0.0} GB total, {available / GiB:0.0} GB currently available.";
        }
        else
        {
            SystemMemoryText = "System memory could not be detected.";
        }

        var options = BuildOptions();
        long budget = MemoryBudget.Resolve(options);
        ComputedBudgetText = budget > 0
            ? $"Backup buffer budget: {budget / GiB:0.00} GB"
            : "Backup buffer budget: disabled (files read directly from disk).";
    }

    private MemoryBudgetOptions BuildOptions()
    {
        var options = new MemoryBudgetOptions
        {
            Mode = _isAuto ? MemoryBudgetMode.Auto : MemoryBudgetMode.Fixed,
        };

        if (int.TryParse(_percentOfTotal, NumberStyles.Integer, CultureInfo.CurrentCulture, out var pct))
            options.PercentOfTotal = Math.Clamp(pct, 0, 100);
        if (double.TryParse(_reserveGb, NumberStyles.Float, CultureInfo.CurrentCulture, out var reserve))
            options.ReserveGb = Math.Max(0, reserve);
        if (double.TryParse(_fixedGb, NumberStyles.Float, CultureInfo.CurrentCulture, out var fixedGb))
            options.FixedGb = Math.Max(0, fixedGb);

        return options;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        RefreshPreview();
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        _settings.MemoryBudget = BuildOptions();
        _settings.SuppressBackupSuggestions = _suppressBackupSuggestions;
        _settings.Save();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
