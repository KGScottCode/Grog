// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Grog.App.Services;

/// <summary>
/// An ObservableCollection that can swap its whole content with ONE change notification. The device grids
/// held ~7,500 rows at 1,500 games and rebuilt them with Clear + Add per row: thousands of CollectionChanged
/// events, each re-laid out by the ItemsControl (stress run 09-06: 680 ms per rebuild in the view model and
/// seconds in layout).
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        var list = Items;
        list.Clear();
        foreach (var x in items) list.Add(x);
        OnPropertyChanged(new PropertyChangedEventArgs("Count"));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
