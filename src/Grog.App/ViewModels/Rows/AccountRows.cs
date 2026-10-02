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

/// <summary>One owner disc in the grid's Owner column: the account's color + shortest unique initials.</summary>
public sealed record AccountBadge(string OwnerId, string Name, string Initials, IBrush Fill, IBrush Ink);

/// <summary>The account-to-badge registry. COLOR is the identity: a stable color per account, avoiding the
/// status hues; initials extend to the shortest disambiguating prefix. Static: changes only when accounts do.</summary>
public static class OwnerBadges
{
    private static readonly (Color Fill, Color Ink)[] Palette =
    {
        (Color.Parse("#2E5E8C"), Color.Parse("#CFE4F5")),   // steel blue
        (Color.Parse("#5E4E8C"), Color.Parse("#DFD8F2")),   // violet
        (Color.Parse("#2E7A6A"), Color.Parse("#CFF0E8")),   // sea teal
        (Color.Parse("#7A4E6E"), Color.Parse("#F0D8E8")),   // plum
        (Color.Parse("#55606C"), Color.Parse("#DDE3E9")),   // slate
        (Color.Parse("#6E5A3D"), Color.Parse("#EAE0CF")),   // umber (muted, not the action amber)
    };

    public static IReadOnlyDictionary<string, AccountBadge> Map { get; private set; }
        = new Dictionary<string, AccountBadge>();

    /// <summary>Ownership chrome exists only when accounts &gt; 1 (badge the exception, not the rule).</summary>
    public static bool Show { get; private set; }

    public static void Rebuild(IReadOnlyList<(string Id, string Name)> accounts)
    {
        var map = new Dictionary<string, AccountBadge>();
        for (var i = 0; i < accounts.Count; i++)
        {
            var (id, name) = accounts[i];
            var display = string.IsNullOrWhiteSpace(name) ? (id.Length == 0 ? "Primary" : id) : name;
            var (fill, ink) = Palette[i % Palette.Length];
            map[id] = new AccountBadge(id, display, InitialsFor(display, accounts, i),
                new SolidColorBrush(fill), new SolidColorBrush(ink));
        }
        Map = map;
        Show = accounts.Count > 1;
    }

    /// <summary>Shortest prefix of THIS name that no other account's name shares (case-insensitive),
    /// capped at 3 characters -- past that the color has to carry it and the tooltip has the rest.</summary>
    private static string InitialsFor(string display, IReadOnlyList<(string Id, string Name)> accounts, int self)
    {
        for (var len = 1; len <= Math.Min(3, display.Length); len++)
        {
            var prefix = display[..len];
            var clash = false;
            for (var j = 0; j < accounts.Count; j++)
            {
                if (j == self) continue;
                var other = string.IsNullOrWhiteSpace(accounts[j].Name)
                    ? (accounts[j].Id.Length == 0 ? "Primary" : accounts[j].Id) : accounts[j].Name;
                if (other.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { clash = true; break; }
            }
            if (!clash) return prefix.ToUpperInvariant();
        }
        return display[..Math.Min(3, display.Length)].ToUpperInvariant();
    }

    /// <summary>When the Owner FILTER is narrowing the grid, only the checked accounts' discs render.
    /// Null = no narrowing, every owner shows. Set by ApplyFilter alongside the row filter.</summary>
    public static IReadOnlySet<string>? VisibleOwners { get; set; }

    /// <summary>Badges for a file/game owner list, in registry (registration) order -- primary first.</summary>
    public static IReadOnlyList<AccountBadge> For(IEnumerable<string> ownerIds)
    {
        if (!Show) return Array.Empty<AccountBadge>();
        var set = ownerIds as ICollection<string> ?? ownerIds.ToList();
        var list = new List<AccountBadge>();
        foreach (var b in Map.Values)
            if (set.Contains(b.OwnerId) && (VisibleOwners is null || VisibleOwners.Contains(b.OwnerId)))
                list.Add(b);
        return list;
    }
}

/// <summary>One account row in the account panel.</summary>
/// <param name="Connected">Whether this account has a live session; drives the card's state text and the
/// Log Out / Log In button flip.</param>
/// <param name="BacksInteractiveSession">True for the account whose token file the in-memory _auth session
/// reads and writes (the FIRST registered account, RebindInteractiveSession). Log-out/remove of THIS row
/// must also drop the live session; every other row only touches its own file.</param>
/// <param name="BackedUpGames">Games of this owner with any local copy; -1 = not computed (segment omitted).
/// (UX 09-08 #1)</param>
/// <param name="LastChecked">Manifest LastSyncCompleted as relative text; null = never / unknown (segment omitted).
/// (UX 09-08 #1)</param>
public sealed record AccountRow(string Id, string Name, int ItemCount, bool Visible, bool Connected = true,
    bool BacksInteractiveSession = false, int BackedUpGames = -1, string? LastChecked = null)
{
    public string CountText => Connected ? $"{ItemCount} items" : $"{ItemCount} items - signed out";
    /// <summary>The row's subline (UX 09-08 #1): "{N} items · {M} games backed up · last checked {ago}", with a
    /// trailing "signed out" when the session is gone. Segments whose figure is unavailable are omitted.</summary>
    public string StatsText
    {
        get
        {
            var parts = new List<string> { $"{ItemCount} items" };
            if (BackedUpGames >= 0) parts.Add($"{BackedUpGames} game{(BackedUpGames == 1 ? "" : "s")} backed up");
            if (!string.IsNullOrEmpty(LastChecked)) parts.Add($"last checked {LastChecked}");
            if (!Connected) parts.Add("signed out");
            return string.Join(" \u00B7 ", parts);
        }
    }
    /// <summary>This account's grid disc, shown on its card too - the Accounts page is the LEGEND for
    /// "which disc is which". Null until the registry knows this account.</summary>
    public AccountBadge? Badge => OwnerBadges.Map.TryGetValue(Id, out var b) ? b : null;
    public bool HasBadge => Badge is not null;
    public string ActionText => Connected ? "Log Out" : "Log In";
    /// <summary>Signed out: Log In is the row's remedy and must not read as a quiet verb, but every
    /// signed-out row taking the filled tier would breach one-filled-primary. Amber ghost tier for all.</summary>
    public bool ActionIsPrimary => false;
    public bool ActionIsAttn => !Connected;
    public string ActionTip => Connected
        ? "Sign out of this account. It stays listed and its downloaded backups stay on disk."
        : "Open a GOG login window and sign back in.";
}
