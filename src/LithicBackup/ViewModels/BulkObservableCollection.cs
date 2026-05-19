using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace LithicBackup.ViewModels;

/// <summary>
/// <see cref="ObservableCollection{T}"/> subclass that supports bulk
/// operations with a single <see cref="NotifyCollectionChangedAction.Reset"/>
/// notification, avoiding per-item layout storms in WPF controls like TreeView.
/// </summary>
public class BulkObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppressNotifications;

    /// <summary>
    /// Replace all items in the collection, firing a single Reset notification
    /// instead of individual Add/Remove events per item.
    /// </summary>
    public void ReplaceAll(IList<T> items)
    {
        CheckReentrancy();
        _suppressNotifications = true;
        try
        {
            Items.Clear();
            foreach (var item in items)
                Items.Add(item);
        }
        finally
        {
            _suppressNotifications = false;
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppressNotifications)
            base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_suppressNotifications)
            base.OnPropertyChanged(e);
    }
}
