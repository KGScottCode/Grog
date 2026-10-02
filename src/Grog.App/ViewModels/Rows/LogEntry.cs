// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
// Row and support types the views bind to; shares the MainWindowViewModel namespace on purpose.
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grog.Core.Models;
using Grog.Core.Download;
using Grog.Core.Sync;
using Grog.Core.Format;

namespace Grog.App.ViewModels;

/// <summary>What kind of activity a log line records. General covers everything that isn't a download or
/// move (scans, login, folder changes). Exists for the Log() method signature; callers pass a category.</summary>
public enum LogCategory { General = 0, Download = 1, Move = 2, Verify = 3, Scan = 4, Auth = 5, Cloud = 6 }

public enum LogSeverity { Info = 0, Warn = 1, Error = 2 }

/// <summary>One line in the Health view's Activity list: what happened, when, and how serious.</summary>
public sealed record LogEntry(System.DateTimeOffset Time, LogCategory Category, LogSeverity Severity, string Message)
{
    public string TimeText => Time.ToLocalTime().ToString("HH:mm:ss");
    public IBrush SeverityBrush => ViewModels.SeverityBrush.For(Severity);
    public string CategoryText => Category switch
    {
        LogCategory.Download => "Download",
        LogCategory.Move => "Move",
        LogCategory.Verify => "Verify",
        LogCategory.Scan => "Scan",
        LogCategory.Auth => "Auth",
        LogCategory.Cloud => "Cloud",
        _ => "",
    };
    public bool HasCategory => CategoryText.Length > 0;
}
