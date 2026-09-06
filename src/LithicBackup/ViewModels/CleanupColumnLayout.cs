using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LithicBackup.Services;

namespace LithicBackup.ViewModels;

/// <summary>
/// The widths of Cleanup's result columns, shared by the column header and every
/// tree row so dragging one moves the other.
///
/// <para>It is a resource object rather than a property on the view model
/// because the row template is a <see cref="ControlTemplate"/>: a
/// <see cref="ColumnDefinition"/> inside one is not in the visual tree, so
/// <c>RelativeSource AncestorType</c> cannot reach the view model from there.
/// <c>StaticResource</c> resolves at parse time and works anywhere.</para>
///
/// <para><b>Only Files and Size have widths here.</b> Directory is a star column
/// that takes whatever is left, so narrowing these two widens it directly — which
/// is the whole point. The gain is much larger than the pixels suggest: the
/// directory tree indents 19px per level, so at five levels deep a ~160px name
/// column has only ~65px left for text, and handing it back Files+Size (~136px)
/// takes that to ~201px. Roughly three times the readable width, not the 85%
/// that comparing column totals implies.</para>
/// </summary>
public sealed class CleanupColumnLayout : INotifyPropertyChanged
{
    /// <summary>Small enough to drag almost away, wide enough to still grab.</summary>
    public const double MinimumWidth = 18;

    private const double DefaultFilesWidth = 56;
    private const double DefaultSizeWidth = 80;

    private GridLength _filesWidth = new(DefaultFilesWidth);
    private GridLength _sizeWidth = new(DefaultSizeWidth);

    /// <summary>
    /// Coalesces a drag into one write. A GridSplitter raises a change per mouse
    /// move, and settings are a file.
    /// </summary>
    private readonly DispatcherTimer _saveDebounce;

    public CleanupColumnLayout()
    {
        _saveDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(600),
        };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            Save();
        };

        try
        {
            var settings = UserSettings.Load();
            _filesWidth = new GridLength(Clamp(settings.CleanupFilesColumnWidth, DefaultFilesWidth));
            _sizeWidth = new GridLength(Clamp(settings.CleanupSizeColumnWidth, DefaultSizeWidth));
        }
        catch
        {
            // Defaults are already in place.
        }
    }

    public GridLength FilesWidth
    {
        get => _filesWidth;
        set => SetWidth(ref _filesWidth, value);
    }

    public GridLength SizeWidth
    {
        get => _sizeWidth;
        set => SetWidth(ref _sizeWidth, value);
    }

    private static double Clamp(double value, double fallback)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < MinimumWidth)
            return fallback;
        return value > 4000 ? fallback : value;
    }

    private void SetWidth(ref GridLength field, GridLength value, [CallerMemberName] string? name = null)
    {
        // A splitter can hand back Auto or Star mid-drag; only pixel widths are
        // meaningful for a column the rows must match exactly.
        if (!value.IsAbsolute || value.Value < MinimumWidth)
            return;
        if (field.IsAbsolute && Math.Abs(field.Value - value.Value) < 0.5)
            return;

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private void Save()
    {
        try
        {
            var settings = UserSettings.Load();
            settings.CleanupFilesColumnWidth = _filesWidth.Value;
            settings.CleanupSizeColumnWidth = _sizeWidth.Value;
            settings.Save();
        }
        catch
        {
            // A column width that cannot be persisted is not worth failing over.
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
