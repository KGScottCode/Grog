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

public sealed record BackupLocationRow(
    string RootId, string Label, string Path, bool IsPrimary, bool IsOnline,
    long GrogBytes, long FreeBytes, long TotalBytes, int FileCount, string FitColor)
{
    public string DetachLabel => IsPrimary ? "Forget…" : "Detach…";
    public string Name { get; init; } = "";
    public bool ShowName { get; init; }
    public string NameInParens => $"({Name})";
    public string DriveName => string.IsNullOrEmpty(Name) ? Label : Name;
    public string RenameTip => ShowName ? $"Rename {Name}" : "Name this storage";
    public bool HoldsGames { get; init; }
    public bool HoldsExtras { get; init; }
    public IReadOnlyList<ExtraTypeSegment> Composition { get; init; } = Array.Empty<ExtraTypeSegment>();
    public bool HasComposition => Composition.Count > 1;   // one bucket = a solid bar saying nothing
    public bool NothingBackedUpHere => FileCount == 0;

    // Fixed slots, because a proportional bar needs star-sized grid columns and those can't be generated
    // from a collection. Five buckets is plenty; unused slots collapse to zero width.
    private ExtraTypeSegment? Seg(int i) => i < Composition.Count ? Composition[i] : null;
    private static readonly Avalonia.Controls.GridLength Zero = new(0, Avalonia.Controls.GridUnitType.Star);
    public Avalonia.Controls.GridLength Comp0 => Seg(0)?.Col ?? Zero;
    public Avalonia.Controls.GridLength Comp1 => Seg(1)?.Col ?? Zero;
    public Avalonia.Controls.GridLength Comp2 => Seg(2)?.Col ?? Zero;
    public Avalonia.Controls.GridLength Comp3 => Seg(3)?.Col ?? Zero;
    public Avalonia.Controls.GridLength Comp4 => Seg(4)?.Col ?? Zero;
    public IBrush? CompBrush0 => Seg(0)?.Brush;
    public IBrush? CompBrush1 => Seg(1)?.Brush;
    public IBrush? CompBrush2 => Seg(2)?.Brush;
    public IBrush? CompBrush3 => Seg(3)?.Brush;
    public IBrush? CompBrush4 => Seg(4)?.Brush;
    // ---- CAPACITY bar: the whole DRIVE is the denominator ----
    // Distinct from the composition bar above, which normalizes to Grog's own content. This one answers
    // "how full is this drive, and where will the queue leave it": other data, what Grog holds, what the
    // queue plans to add, then what is left.
    public long QueuedBytes { get; init; }
    public bool HasUnplaceableQueue { get; init; }
    public long UnplaceableBytes { get; init; }
    public int UnplaceableCount { get; init; }
    public string UnplaceableLine => FitsNowhereLine(UnplaceableCount, UnplaceableBytes);
    public static string FitsNowhereLine(int count, long bytes)
        => $"{count} file{(count == 1 ? "" : "s")} / "
         + $"{Grog.Core.Format.ByteFormat.Size(bytes)} won't fit and will not download.";
    public IBrush QueuedSegmentBrush => HasUnplaceableQueue
        ? Palette.ErrorRed
        : RowBrushes.Translucent(Palette.AccentAmber, 0x80);

    public bool ShowCapacityBar => IsOnline && TotalBytes > 0;
    public long SiblingGrogBytes { get; init; }
    public long OtherBytes => Math.Max(0, TotalBytes - FreeBytes - GrogBytes - SiblingGrogBytes);
    public bool OtherIsOverhead => IsOnline && TotalBytes > 0 && OtherBytes * 100 < TotalBytes;
    public string BarToolTip
    {
        get
        {
            if (!IsOnline || TotalBytes <= 0) return "";
            var f = Grog.Core.Format.ByteFormat.Size;
            var other = OtherIsOverhead ? $"{f(OtherBytes)} drive overhead (filesystem bookkeeping; Explorer counts it as used)"
                                        : $"{f(OtherBytes)} other data (not Grog's)";
            var queued = QueuedShown > 0 ? $" · {f(QueuedShown)} queued here" : "";
            // Name the sibling explicitly: without it the numbers do not add up to "used" and the drive looks
            // like it is hiding something.
            var sibling = SiblingGrogBytes > 0 ? $" · {f(SiblingGrogBytes)} Grog in another folder here" : "";
            return $"{f(TotalBytes - FreeBytes)} used of {f(TotalBytes)} (as Explorer reports): {f(GrogBytes)} Grog{sibling} · {other}{queued} · {f(FreeBytes)} free";
        }
    }
    private long QueuedShown => Math.Max(0, Math.Min(QueuedBytes, FreeBytes));
    private long FreeAfterQueue => Math.Max(0, FreeBytes - QueuedShown);
    private Avalonia.Controls.GridLength Slice(long bytes) =>
        new(TotalBytes <= 0 ? 0.0001 : Math.Max(0.0001, (double)bytes / TotalBytes), Avalonia.Controls.GridUnitType.Star);
    public Avalonia.Controls.GridLength CapOtherCol  => Slice(OtherBytes);
    public Avalonia.Controls.GridLength CapGrogCol   => Slice(GrogBytes);
    public Avalonia.Controls.GridLength CapQueuedCol => Slice(QueuedShown);
    public Avalonia.Controls.GridLength CapFreeCol   => Slice(FreeAfterQueue);
    public bool HasQueuedSegment => QueuedShown > 0;

    public string CapacityCaption
    {
        get
        {
            var free = Grog.Core.Format.ByteFormat.Size(FreeBytes);
            if (QueuedShown > 0)
            {
                var after = Grog.Core.Format.ByteFormat.Size(FreeAfterQueue);
                return $"{free} free · {Grog.Core.Format.ByteFormat.Size(QueuedShown)} queued here · {after} left after";
            }
            return $"{free} free of {Grog.Core.Format.ByteFormat.Size(TotalBytes)}";
        }
    }

    public string PolicyLine { get; init; } = "";
    public bool IsViewing { get; init; }

    public bool Removable { get; init; }
    public bool JustReattached { get; init; }
    public bool StatusKnown { get; init; } = true;
    public bool IsDisconnected => StatusKnown && !IsOnline && Removable;
    public string StatusWord => !StatusKnown ? "" : JustReattached ? "Reconnected" : IsOnline ? "Connected" : Removable ? "Disconnected" : "Offline";
    public string VerifyText { get; init; } = "";
    public bool IsVerifying => VerifyText.Length > 0;
    public string DisconnectedNotice =>
        "This removable drive is unplugged. The files below are what was on it last time - reconnect it to back up or move them. Nothing is lost.";
    public string RemovableButtonText => Removable ? "✓ Removable" : "Mark as Removable";
    public string RemovableTip => Removable
        ? "This folder is on a removable drive (USB / external). Its files show as Disconnected - not Missing - when it's unplugged. Click to unmark."
        : "Mark this folder's drive as removable (USB / external) so its files aren't flagged Missing when it's unplugged.";

    private static string F(long b) => Grog.Core.Format.ByteFormat.Size(b);
    public string CoverageLine { get; init; } = "";
    public string UsageLine => CoverageLine.Length > 0
        ? CoverageLine
        : $"{F(GrogBytes)} backed up · {FileCount} file{(FileCount == 1 ? "" : "s")}";
    private string OfflineWord => !StatusKnown ? "" : Removable ? "disconnected" : "drive offline";
    public string CapacityLine => IsOnline ? $"{F(FreeBytes)} free of {F(TotalBytes)}" : OfflineWord;
    // Split halves so the VALUES stay white and the connecting words go dim (value outweighs label).
    public string CapacityFreeText  => F(FreeBytes);
    public string CapacityTotalText => F(TotalBytes);
    public string GrogSizeText => F(GrogBytes);
    public double GrogPercent => IsOnline && TotalBytes > 0 ? Math.Clamp(100.0 * GrogBytes / TotalBytes, 0, 100) : 0;
    public double UsedPercent => IsOnline && TotalBytes > 0 ? Math.Clamp(100.0 * (TotalBytes - FreeBytes) / TotalBytes, 0, 100) : 0;
    // Capacity bar as three proportional zones (deterministic -- avoids ProgressBar overlay quirks).
    public Avalonia.Controls.GridLength GrogCol => new(GrogPercent, Avalonia.Controls.GridUnitType.Star);
    public Avalonia.Controls.GridLength OtherCol => new(Math.Max(0, UsedPercent - GrogPercent), Avalonia.Controls.GridUnitType.Star);
    // The QUEUE shows on the Overview bar too - the owner caught the two pages disagreeing (Folders
    // showed a rim-full red bar while Overview showed a nearly empty drive). Same data, same color
    // logic (QueuedSegmentBrush goes red when part of the queue fits nowhere), carved out of free.
    private double QueuedPercent => TotalBytes <= 0 ? 0 : Math.Min(100 - UsedPercent, (double)QueuedShown / TotalBytes * 100);
    public Avalonia.Controls.GridLength QueuedCol => new(Math.Max(0, QueuedPercent), Avalonia.Controls.GridUnitType.Star);
    public Avalonia.Controls.GridLength FreeCol => new(Math.Max(0.0001, 100 - UsedPercent - QueuedPercent), Avalonia.Controls.GridUnitType.Star);
    public bool IsProblem => StatusKnown && !IsOnline && !Removable;
    // Green = connected; muted = safely disconnected (removable, unplugged); red = offline and NOT removable.
    // Neutral gray until status is known, so a card never opens on a red/orange claim it has to walk back.
    public IBrush StateBrush => !StatusKnown ? Palette.InkMuted
        : IsOnline ? Palette.SuccessGreen : IsDisconnected ? Palette.WarnOrange : Palette.ErrorRed;
    public IBrush StatusPillBg => IsDisconnected ? Palette.SurfaceWarnDark2 : Palette.SurfaceErrorDark2;


}
