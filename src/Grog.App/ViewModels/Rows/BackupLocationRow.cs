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

/// <summary>One backup location card: capacity + how much Grog holds there.</summary>
public sealed record BackupLocationRow(
    string RootId, string Label, string Path, bool IsPrimary, bool IsOnline,
    long GrogBytes, long FreeBytes, long TotalBytes, int FileCount, string FitColor)
{
    /// <summary>Which content roles this device holds (By-content mode). Highlights the role chips.</summary>
    /// <summary>The card's take-out button (09-13): the primary cannot be detached, so it says Forget; every other
    /// storage says Detach. The dialog it opens uses the same word.</summary>
    public string DetachLabel => IsPrimary ? "Forget…" : "Detach…";
    /// <summary>(09-15) The user's name for the drive (root.Label), shown before the path only when it is not just
    /// the folder name. Set by the builder.</summary>
    public string Name { get; init; } = "";
    public bool ShowName { get; init; }
    public string NameInParens => $"({Name})";
    /// <summary>The drive by its own name (custom label or folder name), never the slot word: for log lines
    /// about a slot change, where "Secondary is now the primary" says nothing once the roles have moved (09-16).</summary>
    public string DriveName => string.IsNullOrEmpty(Name) ? Label : Name;
    public string RenameTip => ShowName ? $"Rename {Name}" : "Name this storage";
    public bool HoldsGames { get; init; }
    public bool HoldsExtras { get; init; }
    /// <summary>What Grog put here, by bucket, normalized to Grog's OWN content -- NOT to the drive
    /// (splitting the capacity bar by type gives invisible hairlines). This bar answers "what's on it".</summary>
    public IReadOnlyList<ExtraTypeSegment> Composition { get; init; } = Array.Empty<ExtraTypeSegment>();
    public bool HasComposition => Composition.Count > 1;   // one bucket = a solid bar saying nothing
    /// <summary>The card's "nothing here yet" placeholder keys on FILES, not on the bar's own visibility:
    /// with exactly one content bucket the bar hides (solid bar says nothing) but files ARE here, and
    /// borrowing !HasComposition made the card claim "Nothing backed up here yet" under a header counting
    /// 1 game / 33 MB (MacinCloud 2026-08-28).</summary>
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
    /// <summary>Queued bytes the planner assigned to THIS root. Never exceeds its free space: files with
    /// nowhere to go get no root at all, which is what <see cref="HasUnplaceableQueue"/> reports.</summary>
    public long QueuedBytes { get; init; }
    /// <summary>Somewhere in the library, queued files fit on no device. A drive-level bar cannot show this
    /// (the bytes were never assigned here), so it colors the bar instead of adding a phantom segment.</summary>
    public bool HasUnplaceableQueue { get; init; }
    /// <summary>How much of the queue fits on no device at all. Named in the warning, because "some of it
    /// won't fit" is ignorable and "18.2 GB won't fit" is not.</summary>
    public long UnplaceableBytes { get; init; }
    /// <summary>How many queued files that shortfall is made of - the count leads so the warning opens
    /// with the scale ("41 files / 170.8 GB"), not just an abstract byte figure.</summary>
    public int UnplaceableCount { get; init; }
    public string UnplaceableLine => FitsNowhereLine(UnplaceableCount, UnplaceableBytes);
    /// <summary>THE fits-nowhere sentence (Storage row + Overview copy): count leads, bytes follow.</summary>
    public static string FitsNowhereLine(int count, long bytes)
        => $"{count} file{(count == 1 ? "" : "s")} / "
         + $"{Grog.Core.Format.ByteFormat.Size(bytes)} won't fit and will not download.";
    /// <summary>The queued segment turns red once part of the queue has no home: the bar is packed to the
    /// rim and that is a problem, not a snug fit.</summary>
    public IBrush QueuedSegmentBrush => HasUnplaceableQueue
        ? Palette.ErrorRed
        : RowBrushes.Translucent(Palette.AccentAmber, 0x80);

    public bool ShowCapacityBar => IsOnline && TotalBytes > 0;
    /// <summary>Grog bytes held by OTHER backup folders on the SAME physical volume. Two folders on one drive
    /// share Free and Total, so "used" (Total - Free) contains BOTH folders' Grog data; subtracting only this
    /// row's own would report the sibling's backups as somebody else's files. Zero for a drive holding one
    /// backup folder, which is why the error hid until a second folder was added to the same drive.</summary>
    public long SiblingGrogBytes { get; init; }
    /// <summary>Data on this volume that is not Grog's. Everything Grog holds here comes out, whichever
    /// folder it sits in.</summary>
    public long OtherBytes => Math.Max(0, TotalBytes - FreeBytes - GrogBytes - SiblingGrogBytes);
    /// <summary>The bar's used + free equal Explorer's; the split is ours. When "other" is under 1% of the
    /// volume on a drive that holds Grog, it is the filesystem's own bookkeeping (MFT, logs, recycle bin), which
    /// Explorer folds into used without a name - name it, or the user hunts for files that do not exist.</summary>
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

    /// <summary>One line under the bar: what is free now, what the queue will take, and the shortfall when
    /// some of it fits nowhere.</summary>
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

    /// <summary>Where new files land, stated plainly. Policy, not a rule with exceptions: you can always move.</summary>
    public string PolicyLine { get; init; } = "";
    public bool IsViewing { get; init; }

    /// <summary>User-declared: this device detaches (USB / external). When it's unplugged its files read
    /// "Disconnected" (safe), not "Missing".</summary>
    public bool Removable { get; init; }
    /// <summary>Set briefly (~2s) when a removable drive comes back, for the green "Reconnected" flash.</summary>
    public bool JustReattached { get; init; }
    /// <summary>True once the drive state is trustworthy (services up + a real backup location). Until then
    /// the card shows NO status at all -- better nothing briefly than a wrong status corrected later.</summary>
    public bool StatusKnown { get; init; } = true;
    /// <summary>Offline BUT removable = safely detached, not a problem. Suppressed until status is known.</summary>
    public bool IsDisconnected => StatusKnown && !IsOnline && Removable;
    public string StatusWord => !StatusKnown ? "" : JustReattached ? "Reconnected" : IsOnline ? "Connected" : Removable ? "Disconnected" : "Offline";
    /// <summary>(1282) "Verifying 37 / 192" while this storage's adopted files are being checked; "" otherwise. A verify
    /// defers moves and showed nowhere before (walk 09-30: a queued move "waits for the current scan or verify" over
    /// a card that read plain Connected).</summary>
    public string VerifyText { get; init; } = "";
    public bool IsVerifying => VerifyText.Length > 0;
    /// <summary>Calm, reassuring notice shown on a disconnected removable card: this is expected, the files
    /// are remembered, and plugging the drive back in restores access. NOT the red "problem" treatment.</summary>
    public string DisconnectedNotice =>
        "This removable drive is unplugged. The files below are what was on it last time - reconnect it to back up or move them. Nothing is lost.";
    public string RemovableButtonText => Removable ? "✓ Removable" : "Mark as Removable";
    public string RemovableTip => Removable
        ? "This folder is on a removable drive (USB / external). Its files show as Disconnected - not Missing - when it's unplugged. Click to unmark."
        : "Mark this folder's drive as removable (USB / external) so its files aren't flagged Missing when it's unplugged.";

    private static string F(long b) => Grog.Core.Format.ByteFormat.Size(b);
    /// <summary>Set by the view model, which alone sees the whole library; UsageLine falls back to the
    /// bare quantity when unset.</summary>
    public string CoverageLine { get; init; } = "";
    public string UsageLine => CoverageLine.Length > 0
        ? CoverageLine
        : $"{F(GrogBytes)} backed up · {FileCount} file{(FileCount == 1 ? "" : "s")}";
    private string OfflineWord => !StatusKnown ? "" : Removable ? "disconnected" : "drive offline";
    public string CapacityLine => IsOnline ? $"{F(FreeBytes)} free of {F(TotalBytes)}" : OfflineWord;
    // Split halves so the VALUES stay white and the connecting words go dim (value outweighs label).
    public string CapacityFreeText  => F(FreeBytes);
    public string CapacityTotalText => F(TotalBytes);
    /// <summary>(Storage math 09-09) What Grog holds IN THIS FOLDER, as a size. It replaced a percentage of
    /// the DRIVE in the Storage Status row: on a 952 GB volume holding 252 MB that percentage rounded to
    /// "0.0%", so the row announced nothing was here while 22 files sat in the folder. A folder-scoped
    /// quantity beside a drive-scoped capacity is the honest pairing; the percentage was neither.</summary>
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
    /// <summary>A fixed drive that should be present isn't (offline and NOT user-marked removable) -- the
    /// device error state the rail dot + red card surface; an unplugged removable is IsDisconnected.</summary>
    public bool IsProblem => StatusKnown && !IsOnline && !Removable;
    // Green = connected; muted = safely disconnected (removable, unplugged); red = offline and NOT removable.
    // Neutral gray until status is known, so a card never opens on a red/orange claim it has to walk back.
    public IBrush StateBrush => !StatusKnown ? Palette.InkMuted
        : IsOnline ? Palette.SuccessGreen : IsDisconnected ? Palette.WarnOrange : Palette.ErrorRed;
    public IBrush StatusPillBg => IsDisconnected ? Palette.SurfaceWarnDark2 : Palette.SurfaceErrorDark2;


}
