using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LithicBackup.ViewModels;

/// <summary>
/// Base class for view models with INotifyPropertyChanged support.
/// </summary>
public abstract class ViewModelBase : INotifyPropertyChanged
{
    /// <summary>
    /// Minimum interval in milliseconds between UI progress updates.
    /// All throttled progress reporters should use this value.
    /// </summary>
    protected const int ProgressUpdateIntervalMs = 500;
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}
