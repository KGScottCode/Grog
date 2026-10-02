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

public sealed partial class CloudGameRow : ObservableObject
{
    public CloudGameRow(long gogId, string title, string clientId, long sizeBytes = 0,
        System.DateTimeOffset? lastBackup = null, int localSaveCount = 0, long backedUpSize = 0,
        int files = 0, System.DateTimeOffset? latestChangeUtc = null, int backedUpFiles = 0,
        System.DateTimeOffset? backedUpChangeUtc = null, string accountId = "")
    {
        GogId = gogId; Title = title; ClientId = clientId; SizeBytes = sizeBytes;
        LastBackup = lastBackup; LocalSaveCount = localSaveCount; BackedUpSize = backedUpSize;
        Files = files; LatestChangeUtc = latestChangeUtc; BackedUpFiles = backedUpFiles; BackedUpChangeUtc = backedUpChangeUtc;
        AccountId = accountId;
    }

    /// <summary>The account whose container this row shows (saves are per-account facts). "" = the
    /// tokens.json slot. Downloads for the row go through THIS account's session.</summary>
    public string AccountId { get; }
    public long GogId { get; }
    public string Title { get; }
    public string ClientId { get; }
    public long SizeBytes { get; }
    public System.DateTimeOffset? LastBackup { get; }
    public int LocalSaveCount { get; }
    public long BackedUpSize { get; }
    public int Files { get; }
    public System.DateTimeOffset? LatestChangeUtc { get; }
    public int BackedUpFiles { get; }
    public System.DateTimeOffset? BackedUpChangeUtc { get; }

    // ---- Expandable local save history. Loaded lazily from disk the first time the row is
    //      expanded; the chevron only appears once at least one local save exists to reveal. ----
    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _localSavesLoaded;
    public System.Collections.ObjectModel.ObservableCollection<LocalSaveRow> LocalSaves { get; } = new();
    public bool CanExpand => HasBackup;
    public string ExpandGlyph => IsExpanded ? "▾" : "▸";
    public string ExpandTip => IsExpanded ? "Hide local saves" : "Show local saves";
    /// <summary>True once we've looked and found nothing on disk -- drives the empty-history note.</summary>
    public bool ShowEmptyHistory => LocalSavesLoaded && LocalSaves.Count == 0;
    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ExpandGlyph));
        OnPropertyChanged(nameof(ExpandTip));
    }
    partial void OnLocalSavesLoadedChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyHistory));

    public bool HasBackup => LocalSaveCount > 0;
    /// <summary>Up to date only with a COMPLETE backup of the CURRENT cloud state: every file captured, no
    /// server-side change since (change timestamp, else size+file-count). Uncertainty = "update available".</summary>
    // (S2.2) Core's rule (CloudSaveRun.IsUpToDate), over this row's immutable facts; computed once.
    private bool? _upToDate;
    public bool IsUpToDate => _upToDate ??= Grog.Core.Runs.CloudSaveRun.IsUpToDate(new Grog.Core.Models.CloudAccountSave
    {
        AccountId = AccountId, SizeBytes = SizeBytes, Files = Files, UpdatedUtc = LatestChangeUtc,
        LocalSaveCount = LocalSaveCount, BackedUpSize = BackedUpSize, BackedUpFiles = BackedUpFiles, BackedUpChangeUtc = BackedUpChangeUtc,
    });
    public string ButtonText => HasBackup ? "Download Again" : "Download Save";
    private static string F(long b) => Grog.Core.Format.ByteFormat.Size(b);

    // ---- Cloud row "online -> local" compare: the SAVE's last-changed time on GOG next to OUR last backup
    //      time; the gap between them IS the status, so New/Update/Up-to-date reads at a glance.
    /// <summary>Status as one colored pill: New (never backed up) / Update available / Up to date.</summary>
    public string StatusPillText => !HasBackup ? "New" : IsUpToDate ? "Up to date" : "Update available";
    /// <summary>New + Update available are AMBER (an action: you don't hold the current save); Up to date is green.</summary>
    public IBrush StatusPillFg => IsUpToDate ? Palette.SuccessGreen : Palette.AccentAmberSoft;
    public IBrush StatusPillBg => RowBrushes.Translucent(StatusPillFg, 0x22);
    public IBrush StatusPillBorder => RowBrushes.Translucent(StatusPillFg, 0x55);

    // ---- ONE plain-language evidence line: the PILL is the answer, this carries the WHY. A BEHIND row earns
    //      both halves, an up-to-date row states one calm fact; exact stamps live in the hover tooltip.
    private (string Lead, string Datum1, string Tail, string Datum2) Evidence()
    {
        // Never backed up. GOG does not always give a change timestamp, and a real library is mostly
        // this case -- so the no-timestamp branch must read as a sentence on its own, not "from --".
        if (!HasBackup)
            return LatestChangeUtc is { } c
                ? ("New cloud save from ", RelativeShort(c), " · not backed up yet", "")
                : ("Not backed up yet", "", "", "");

        string backedUp = LastBackup is { } b ? RelativeShort(b) : "";
        // Up to date, or we cannot prove a gap (no timestamp on either side): state the calm single fact.
        if (IsUpToDate || LatestChangeUtc is null || LastBackup is null)
            return ("Backed up ", backedUp, "", "");

        // Behind: the two halves that justify the amber pill.
        return ("Cloud save updated ", RelativeShort(LatestChangeUtc.Value),
                " · your backup is ", AgeShort(LastBackup.Value));
    }

    public string EvidenceLead => Evidence().Lead;
    public string EvidenceDatum1 => Evidence().Datum1;
    public string EvidenceTail => Evidence().Tail;
    public string EvidenceDatum2 => Evidence().Datum2;
    public bool HasEvidenceDatum1 => EvidenceDatum1.Length > 0;
    public bool HasEvidenceTail => EvidenceTail.Length > 0;
    public bool HasEvidenceDatum2 => EvidenceDatum2.Length > 0;

    /// <summary>Staleness phrased as an AGE ("4 days old"), not a point in time ("4 days ago"): the behind-row
    /// reads "your backup is 4 days old", which is the reason to act rather than another date to compare.</summary>
    private static string AgeShort(System.DateTimeOffset t) => Grog.Core.Format.RelativeTime.Old(t);

    /// <summary>The exact stamps, parked on hover: the glance is relative, the precision is on demand. Both
    /// sides appear together here, which is where a behind-row comparison belongs.</summary>
    public string StampTooltip
    {
        get
        {
            string online = LatestChangeUtc is { } c ? $"Cloud save: {c.ToLocalTime():MMM d, yyyy · h:mm tt}" : "";
            string local = LastBackup is { } b ? $"Your backup: {b.ToLocalTime():MMM d, yyyy · h:mm tt}" : "";
            return online.Length > 0 && local.Length > 0 ? online + "\n" + local : online + local;
        }
    }

    /// <summary>Size (+ local save count once we hold one), parenthesised and dim beside the title: context for
    /// the row, never its headline.</summary>
    public string SizeSnapText => HasBackup
        ? $"({F(SizeBytes)} · {LocalSaveCount} local save{(LocalSaveCount == 1 ? "" : "s")})"
        : $"({F(SizeBytes)})";


    private static string RelativeShort(System.DateTimeOffset t) => Grog.Core.Format.RelativeTime.Ago(t);
}

/// <summary>An account section header on the Cloud Saves page (only when more than one account holds
/// saves). Carries the account's disc, name and rollup, and expands/collapses its games.</summary>
public sealed partial class CloudAccountHeaderRow : ObservableObject
{
    public CloudAccountHeaderRow(string accountId, string name, int games, long sizeBytes, bool expanded)
    {
        AccountId = accountId; Name = name;
        RollupText = games == 0
            ? "No cloud saves on this account"
            : $"{games} game{(games == 1 ? "" : "s")} · {Grog.Core.Format.ByteFormat.Size(sizeBytes)} in the cloud";
        _isExpanded = expanded;
    }
    public string AccountId { get; }
    public string Name { get; }
    public string RollupText { get; }
    public AccountBadge? Badge => OwnerBadges.Map.TryGetValue(AccountId, out var b) ? b : null;
    public bool HasBadge => Badge is not null;
    [ObservableProperty] private bool _isExpanded;
    public string Caret => IsExpanded ? "▾" : "▸";
    partial void OnIsExpandedChanged(bool value) => OnPropertyChanged(nameof(Caret));
}

public sealed partial class LocalSaveRow : ObservableObject
{
    public LocalSaveRow(string dir, System.DateTimeOffset stampUtc, int fileCount, long sizeBytes)
    {
        Dir = dir; StampUtc = stampUtc; FileCount = fileCount; SizeBytes = sizeBytes;
    }
    public string Dir { get; }
    public System.DateTimeOffset StampUtc { get; }
    public int FileCount { get; }
    public long SizeBytes { get; }
    public string StampText => StampUtc.ToLocalTime().ToString("MMM d, yyyy · h:mm tt");
    public string DetailText => $"{FileCount} file{(FileCount == 1 ? "" : "s")} · {Grog.Core.Format.ByteFormat.Size(SizeBytes)}";
    [ObservableProperty] private bool _confirmingDelete;
}
