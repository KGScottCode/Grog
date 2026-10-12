// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grog.Core.Api;
using Grog.Core.Auth;
using Grog.Core.Download;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Sync;
using Grog.Core.Verify;
using Grog.Core.Format;

namespace Grog.App.ViewModels;

public partial class MainWindowViewModel
{
    public bool ShowSettings => CurrentView == View.Settings;
    public bool ShowDrivesView => CurrentView == View.Drives;
    public bool ShowCloudView => CurrentView == View.CloudSaves;
    public bool ShowAccountsView => CurrentView == View.Accounts;
    public bool IsLibraryNav => CurrentView is View.Library or View.Updates or View.Missing;

    public string HeaderTitle => CurrentView switch
    {
        View.Drives => "Storage",
        View.CloudSaves => "Cloud Saves",
        View.Overview => "Status",
        View.Accounts => "Accounts",
        View.Settings => "Settings",
        View.About => "About",
        _ => "GOG Library",
    };

    public bool ShowCompositionLine => Library.HasNonGameItems && IsLibraryNav;

    public bool NavLibraryOn => CurrentView == View.Library;
    public bool NavUpdatesOn => CurrentView == View.Updates;
    public bool NavMissingOn => CurrentView == View.Missing;
    public bool NavDrivesOn => CurrentView == View.Drives;
    public bool NavCloudOn => CurrentView == View.CloudSaves;
    public bool NavOverviewOn => CurrentView == View.Overview;
    public bool NavAccountsOn => CurrentView == View.Accounts;
    public bool NavSettingsOn => CurrentView == View.Settings;
    public bool NavAboutOn => CurrentView == View.About;
}
