// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;

namespace Grog.App.Views.Pages;

public partial class LibraryPage : UserControl
{
    public LibraryPage() => InitializeComponent();

    // Column-header filter arrows open their popup anchored to the arrow that was clicked.

    private void OnStatusFilterClick(object? sender, RoutedEventArgs e) => OpenFilterPopup("StatusFilterPopup", sender, e);

    private void OnOwnerFilterClick(object? sender, RoutedEventArgs e) => OpenFilterPopup("OwnerFilterPopup", sender, e);

    private void OnFolderFilterClick(object? sender, RoutedEventArgs e) => OpenFilterPopup("FolderFilterPopup", sender, e);

    private void OnExtrasFilterClick(object? sender, RoutedEventArgs e) => OpenFilterPopup("ExtrasFilterPopup", sender, e);

    private void OpenFilterPopup(string name, object? sender, RoutedEventArgs e)
    {
        if (this.FindControl<Popup>(name) is { } pop && sender is Control c)
        {
            pop.PlacementTarget = c;
            pop.IsOpen = true;
        }
        e.Handled = true; // don't let the click fall through to header sort
    }
}
