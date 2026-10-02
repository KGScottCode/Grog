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
    /// <summary>MANAGE BY EXCEPTION: a passing check is REPORTED, not celebrated -- only a fault or an
    /// outstanding job earns a color. The value stays readable in every case because it is a VALUE.</summary>
    public IBrush ValueBrush => SeverityBrush.For(Level);
    /// <summary>The dot is DECORATION on a quiet row, so it may be muted; otherwise it is the marker.</summary>
    public IBrush DotBrush => Level == Grog.Core.Sync.Severity.Quiet ? Palette.StrokeSubtle : ValueBrush;
}

public sealed record HealthFileRow(string Title, string Name, string SizeText, string Reason = "")
{

    public static HealthFileRow From(LibraryItem item, GameFile f)
    {
        var name = !string.IsNullOrEmpty(f.LocalRelativePath)
            ? System.IO.Path.GetFileName(f.LocalRelativePath) : f.FileKey;
        long bytes = Grog.Core.Sync.Rollups.HeldBytesOf(f);
        // (09-19) A hidden game's problem file is still a problem and still listed and counted; it is not named.
        if (QueueRow.IsHiddenGame(item.GogId))
            return new HealthFileRow(QueueRow.HiddenLabel, "", Grog.Core.Format.ByteFormat.Size(bytes), f.UnavailableReason ?? "");
        return new HealthFileRow(item.Title, name, Grog.Core.Format.ByteFormat.Size(bytes),
                                 f.UnavailableReason ?? "");
    }
}

/// <summary>(Rail exceptions 09-09) One outstanding item on a rail entry: the words, and the SEVERITY that
/// decides both its order in the stack and its colour. It carries the severity rather than a brush so
/// <see cref="SeverityBrush"/> stays the one place a grading becomes a colour (the d234 rule).</summary>
public sealed record RailException(string Text, Grog.Core.Sync.Severity Severity)
{
    public Avalonia.Media.IBrush Brush => SeverityBrush.For(Severity);
}
