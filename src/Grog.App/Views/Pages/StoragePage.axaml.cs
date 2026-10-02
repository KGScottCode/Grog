// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using Avalonia.Controls;
using Grog.App.ViewModels;

namespace Grog.App.Views.Pages;

public partial class StoragePage : UserControl
{
    private StorageViewModel? _vm;

    public StoragePage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Hook();
        Hook();
    }

    /// <summary>(09-14) The rail's "Other storage" entry lands on this page and asks for the section: bring it
    /// into view (it is docked at the bottom, so on a short window it can sit below the fold).</summary>
    private void Hook()
    {
        var vm = DataContext as MainWindowViewModel;
        var storage = vm?.Storage ?? DataContext as StorageViewModel;
        if (ReferenceEquals(storage, _vm)) return;
        if (_vm is not null) _vm.ScrollToOtherStorageRequested -= Reveal;
        _vm = storage;
        if (_vm is not null) _vm.ScrollToOtherStorageRequested += Reveal;
    }

    private void Reveal()
    {
        var section = this.FindControl<Border>("OtherStorageSection");
        section?.BringIntoView();
    }
}
