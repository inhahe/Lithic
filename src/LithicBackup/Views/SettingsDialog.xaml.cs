using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
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
        _burnInPlace = settings.DiscStagingMode == DiscStagingMode.InPlace;
        _checkForUpdates = settings.CheckForUpdates;
        _reconcileMode = settings.ReconcileMode;

        // Seed the continuous-rule editors from the persisted (or default) rules.
        LoadContinuousRows(settings.ContinuousRules ?? new ContinuousRules());
    }

    // ------------------------------------------------------------------
    // Continuous rules (size-tiered debounce + mask-tiered max-wait)
    // ------------------------------------------------------------------

    /// <summary>Editable rows for the debounce (settle-time-by-size) grid.</summary>
    public ObservableCollection<DebounceTierRow> DebounceRows { get; } = new();

    /// <summary>Editable rows for the max-wait (by-name/path-mask) grid.</summary>
    public ObservableCollection<MaxWaitTierRow> MaxWaitRows { get; } = new();

    private void LoadContinuousRows(ContinuousRules rules)
    {
        DebounceRows.Clear();
        foreach (var tier in rules.DebounceTiers ?? new())
        {
            DebounceRows.Add(new DebounceTierRow
            {
                SizeText = FormatSize(tier.MaxSizeBytes),
                DebounceText = FormatSeconds(tier.DebounceSeconds),
            });
        }

        MaxWaitRows.Clear();
        foreach (var tier in rules.MaxWaitTiers ?? new())
        {
            MaxWaitRows.Add(new MaxWaitTierRow
            {
                Name = tier.Name ?? "",
                IncludeText = string.Join(", ", tier.IncludeMasks ?? new()),
                ExcludeText = string.Join(", ", tier.ExcludeMasks ?? new()),
                MaxWaitText = tier.MaxWaitSeconds > 0 ? tier.MaxWaitSeconds.ToString(CultureInfo.CurrentCulture) : "",
            });
        }
    }

    private void AddDebounceRow_Click(object sender, RoutedEventArgs e) =>
        DebounceRows.Add(new DebounceTierRow());

    private void RemoveDebounceRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DebounceTierRow row })
            DebounceRows.Remove(row);
    }

    private void AddMaxWaitRow_Click(object sender, RoutedEventArgs e) =>
        MaxWaitRows.Add(new MaxWaitTierRow());

    private void RemoveMaxWaitRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MaxWaitTierRow row })
            MaxWaitRows.Remove(row);
    }

    private void RestoreContinuousDefaults_Click(object sender, RoutedEventArgs e) =>
        LoadContinuousRows(new ContinuousRules());

    /// <summary>Build a <see cref="ContinuousRules"/> from the current editor rows.</summary>
    private ContinuousRules BuildContinuousRules()
    {
        var debounce = new List<DebounceSizeTier>();
        foreach (var row in DebounceRows)
        {
            // A row needs a settle value to be meaningful; skip fully blank rows.
            if (!TryParseSeconds(row.DebounceText, out var seconds))
                continue;
            debounce.Add(new DebounceSizeTier
            {
                MaxSizeBytes = ParseSize(row.SizeText),
                DebounceSeconds = seconds,
            });
        }
        // Resolution walks tiers smallest-first, so persist them sorted regardless
        // of the order the user arranged the rows in.
        debounce.Sort((a, b) => a.MaxSizeBytes.CompareTo(b.MaxSizeBytes));

        var maxWait = new List<MaxWaitMaskTier>();
        foreach (var row in MaxWaitRows)
        {
            var includes = SplitMasks(row.IncludeText);
            // A tier with no include masks can never match — drop it.
            if (includes.Count == 0)
                continue;
            maxWait.Add(new MaxWaitMaskTier
            {
                Name = (row.Name ?? "").Trim(),
                IncludeMasks = includes,
                ExcludeMasks = SplitMasks(row.ExcludeText),
                MaxWaitSeconds = int.TryParse(row.MaxWaitText, NumberStyles.Integer,
                    CultureInfo.CurrentCulture, out var s) && s > 0 ? s : 0,
            });
        }

        return new ContinuousRules { DebounceTiers = debounce, MaxWaitTiers = maxWait };
    }

    private static List<string> SplitMasks(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new();
        return text
            .Split(new[] { ',', ';', ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(m => m.Trim())
            .Where(m => m.Length > 0)
            .ToList();
    }

    private static string FormatSeconds(double seconds)
    {
        // Show 0.5 not 0.50, and 3 not 3.0.
        return seconds.ToString("0.###", CultureInfo.CurrentCulture);
    }

    private static bool TryParseSeconds(string? text, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out seconds)
               && seconds >= 0;
    }

    /// <summary>Format a byte count as a friendly size; the catch-all tier
    /// (<see cref="long.MaxValue"/>) renders as blank ("everything larger").</summary>
    private static string FormatSize(long bytes)
    {
        if (bytes >= long.MaxValue)
            return "";
        if (bytes <= 0)
            return "0";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double b = bytes;
        int u = 0;
        while (b >= 1024 && u < units.Length - 1)
        {
            b /= 1024;
            u++;
        }
        return $"{b.ToString("0.###", CultureInfo.CurrentCulture)} {units[u]}";
    }

    /// <summary>Parse a friendly size ("1 MB", "500 KB", "2.5 GB", "1048576").
    /// Blank means the catch-all tier (<see cref="long.MaxValue"/>).</summary>
    private static long ParseSize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return long.MaxValue;

        var m = Regex.Match(text.Trim(), @"^\s*([0-9]*\.?[0-9]+)\s*([a-zA-Z]*)\s*$");
        if (!m.Success)
            return long.MaxValue;

        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var val)
            && !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.CurrentCulture, out val))
            return long.MaxValue;

        long mult = m.Groups[2].Value.ToUpperInvariant() switch
        {
            "" or "B" => 1L,
            "K" or "KB" => 1024L,
            "M" or "MB" => 1024L * 1024,
            "G" or "GB" => 1024L * 1024 * 1024,
            "T" or "TB" => 1024L * 1024 * 1024 * 1024,
            _ => 1L,
        };

        double bytes = val * mult;
        if (bytes >= long.MaxValue)
            return long.MaxValue;
        return (long)bytes;
    }

    /// <summary>One row in the debounce (settle-time-by-size) editor.</summary>
    public sealed class DebounceTierRow
    {
        public string SizeText { get; set; } = "";
        public string DebounceText { get; set; } = "";
    }

    /// <summary>One row in the max-wait (by-name/path-mask) editor.</summary>
    public sealed class MaxWaitTierRow
    {
        public string Name { get; set; } = "";
        public string IncludeText { get; set; } = "";
        public string ExcludeText { get; set; } = "";
        public string MaxWaitText { get; set; } = "";
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

    private bool _checkForUpdates;
    public bool CheckForUpdates
    {
        get => _checkForUpdates;
        set { _checkForUpdates = value; OnPropertyChanged(); }
    }

    private ReconcileAfterEditMode _reconcileMode;
    public ReconcileAfterEditMode ReconcileMode
    {
        get => _reconcileMode;
        set
        {
            if (_reconcileMode == value) return;
            _reconcileMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ReconcileAsk));
            OnPropertyChanged(nameof(ReconcileAlways));
            OnPropertyChanged(nameof(ReconcileNever));
        }
    }

    public bool ReconcileAsk
    {
        get => _reconcileMode == ReconcileAfterEditMode.Ask;
        set { if (value) ReconcileMode = ReconcileAfterEditMode.Ask; }
    }

    public bool ReconcileAlways
    {
        get => _reconcileMode == ReconcileAfterEditMode.Always;
        set { if (value) ReconcileMode = ReconcileAfterEditMode.Always; }
    }

    public bool ReconcileNever
    {
        get => _reconcileMode == ReconcileAfterEditMode.Never;
        set { if (value) ReconcileMode = ReconcileAfterEditMode.Never; }
    }

    private bool _burnInPlace;
    public bool BurnInPlace
    {
        get => _burnInPlace;
        set
        {
            if (_burnInPlace == value) return;
            _burnInPlace = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StageToTemp));
        }
    }

    public bool StageToTemp
    {
        get => !_burnInPlace;
        set => BurnInPlace = !value;
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
        _settings.CheckForUpdates = _checkForUpdates;
        _settings.ReconcileMode = _reconcileMode;
        _settings.DiscStagingMode = _burnInPlace
            ? DiscStagingMode.InPlace
            : DiscStagingMode.TemporaryCopy;
        _settings.ContinuousRules = BuildContinuousRules();
        _settings.Save();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
