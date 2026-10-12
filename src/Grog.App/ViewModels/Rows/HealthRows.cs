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

public sealed record HealthCheckRow(string Label, string Value, Grog.Core.Sync.Severity Level, string Tip = "")
{
    public IBrush ValueBrush => SeverityBrush.For(Level);
    public IBrush DotBrush => Level == Grog.Core.Sync.Severity.Quiet ? Palette.StrokeSubtle : ValueBrush;
}

public sealed record HealthFileRow(string Title, string Name, string SizeText, string Reason = "")
{

    public static HealthFileRow From(LibraryItem item, GameFile f)
    {
        var name = !string.IsNullOrEmpty(f.LocalRelativePath)
            ? System.IO.Path.GetFileName(f.LocalRelativePath) : f.FileKey;
        long bytes = Grog.Core.Sync.Rollups.HeldBytesOf(f);
        // A hidden game's problem file is still a problem and still listed and counted; it is not named.
        if (QueueRow.IsHiddenGame(item.GogId))
            return new HealthFileRow(QueueRow.HiddenLabel, "", Grog.Core.Format.ByteFormat.Size(bytes), f.UnavailableReason ?? "");
        return new HealthFileRow(item.Title, name, Grog.Core.Format.ByteFormat.Size(bytes),
                                 f.UnavailableReason ?? "");
    }
}

public sealed record RailException(string Text, Grog.Core.Sync.Severity Severity)
{
    public Avalonia.Media.IBrush Brush => SeverityBrush.For(Severity);
}
