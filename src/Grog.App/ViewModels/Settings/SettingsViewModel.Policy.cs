// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;

namespace Grog.App.ViewModels;

// Backup policy (Settings): where extras route, how the extras tree is shaped, the folder preview and the
// WHAT / WHERE / HOW summary. Reads the storage rows through the root's Storage; the layout offer runs there.
public sealed partial class SettingsViewModel
{
    // ---- WHERE: extras routing. Games always target the primary; overflow is always on. The only exposed
    //      choice is "extras with the games" vs "in a separate folder", and only with both scopes + 2 folders. ----
    /// <para>ONE gate with the card (sweep 2 #20): the card's storage row shows with extras in scope and a second
    /// drive, games in scope or not, and Apply commits that routing. This also asked for games in scope, so with
    /// scope = Extras only the card staged "Secondary" while the preview drew the extras on the primary, hid the
    /// secondary column and the policy line dropped the WHERE.</para>
    public bool ShowExtrasLocation => _library.IncludeExtras && ExtrasCard.ShowStorageRow;

    /// <summary>True when extras route to a folder other than the primary (the "separate folder" pick).</summary>
    public bool ExtrasSeparateLocation
    {
        get
        {
            if (_manifest is null) return false;
            var primary = _manifest.Current.PrimaryRootId;
            return _manifest.Current.Routing.RoleRootOrPrimary(Grog.Core.Models.ContentRole.Extras, primary) != primary;
        }
    }
    public bool ExtrasWithGames => !ExtrasSeparateLocation;

    // ---- Settings "Which folder holds the Extras" row: aliases over the routing above so there is ONE
    //      source of truth; RaisePolicyState raises these alongside the underlying properties. ----
    /// <summary>Show the "Which folder holds the Extras" choice: same gate as the folder preview.</summary>
    public bool ShowExtrasFolderChoice => ShowExtrasLocation;
    /// <summary>The "Primary (with games)" pill is selected.</summary>
    public bool ExtrasOnPrimary => ExtrasWithGames;
    /// <summary>The "Secondary folder" pill is selected.</summary>
    public bool ExtrasOnSecondary => ExtrasSeparateLocation;

    /// <summary>Sub-label stating where extras land under the current pick, naming the secondary folder.</summary>
    // Kept SHORT: it sits inline beside its label, and a long hint wraps and crowds the pills.
    public string ExtrasFolderHint => ExtrasSeparateLocation
        ? $"In {SecondaryFolderLabel}."
        : "With the games.";

    /// <summary>Display label for the non-primary folder ("Secondary" if it has no name yet).</summary>
    // ONE secondary lookup, the card's (sweep 2 refactor): this was a second copy of "first location that is not
    // the primary", free to disagree with the card it sits beside.
    private string SecondaryFolderLabel => ExtrasCard.SecondaryName ?? "the secondary storage";


    // Individual pieces of the Backups policy strip, labeled WHAT / WHERE / HOW to echo Settings.
    public string PolicyWhat => _library.IncludeGames && _library.IncludeExtras ? "Games + Extras"
        : _library.IncludeGames ? "Games only" : _library.IncludeExtras ? "Extras only" : "Nothing selected";
    // WithGame overrides the routing pick: extras follow their game, so the WHERE line must say so even if
    // the (ignored, disabled) Extras role root still points at a secondary folder.
    public string PolicyWhere => ExtrasSeparateLocation && ExtrasSeparateTreeActive
        ? "extras in a separate folder" : "extras with the games";
    public string PolicyHow => ExtrasLayoutValue switch
    {
        Grog.Core.Models.ExtrasPlacement.WithGame => "extras with each game",
        Grog.Core.Models.ExtrasPlacement.SeparateByGame => "extras by game",
        _ => "extras by type",
    };

    // ---- Extras layout, two levels: WHERE (with each game / separate tree), then HOW the separate tree
    //      is ordered (by game / by type). The HOW block is disabled while WithGame is active. ----
    public bool ExtrasWithGameActive => ExtrasLayoutValue == Grog.Core.Models.ExtrasPlacement.WithGame;
    public bool ExtrasSeparateTreeActive => !ExtrasWithGameActive;
    public bool NestedActive => ExtrasLayoutValue == Grog.Core.Models.ExtrasPlacement.SeparateByGame;   // "By game" -> Extras/<game>/<type>
    public bool GroupedActive => ExtrasLayoutValue == Grog.Core.Models.ExtrasPlacement.SeparateByType;  // "By type" -> Extras/<type>/<game>

    // Routing to a second folder only has meaning for the separate tree: with WithGame active the pills are
    // disabled (dimmed in place, owner 08-30: the dimmed state explains itself; no note).
    public bool ExtrasRoutingEnabled => ExtrasSeparateTreeActive;

    /// <summary>Tells the folder preview + policy strip there is more than one folder.</summary>
    public bool ShowModePicker => _root.Storage.BackupLocations.Count >= 2;

    /// <summary>Just the preview: the staged card moved, nothing is committed yet.</summary>
    internal void RaiseFolderPreview()
    {
        OnPropertyChanged(nameof(FolderPreviewPrimary)); OnPropertyChanged(nameof(FolderPreviewSecondary));
        OnPropertyChanged(nameof(ShowPreviewSecondary)); OnPropertyChanged(nameof(FolderPreviewCaption));
        OnPropertyChanged(nameof(FolderPreviewEyebrow));
    }

    internal void RaisePolicyState()
    {
        ExtrasCard.OnLocationsChanged();   // every storage refresh lands here: the staged card re-reads the drives
        OnPropertyChanged(nameof(ShowModePicker)); OnPropertyChanged(nameof(ShowExtrasLocation));
        OnPropertyChanged(nameof(ExtrasSeparateLocation)); OnPropertyChanged(nameof(ExtrasWithGames));
        OnPropertyChanged(nameof(ShowExtrasFolderChoice)); OnPropertyChanged(nameof(ExtrasOnPrimary));
        OnPropertyChanged(nameof(ExtrasOnSecondary)); OnPropertyChanged(nameof(ExtrasFolderHint));
        OnPropertyChanged(nameof(NestedActive)); OnPropertyChanged(nameof(GroupedActive));
        OnPropertyChanged(nameof(ExtrasWithGameActive)); OnPropertyChanged(nameof(ExtrasSeparateTreeActive));
        OnPropertyChanged(nameof(ExtrasRoutingEnabled));
        OnPropertyChanged(nameof(PolicyWhat)); OnPropertyChanged(nameof(PolicyWhere)); OnPropertyChanged(nameof(PolicyHow));
        OnPropertyChanged(nameof(PolicySummaryText));
        OnPropertyChanged(nameof(FolderPreviewPrimary)); OnPropertyChanged(nameof(FolderPreviewSecondary));
        OnPropertyChanged(nameof(FolderPreviewCaption)); OnPropertyChanged(nameof(ShowPreviewSecondary));
    }

    // ---- Live folder preview (Settings): a sample tree that reflects What + Where + How. The preview IS
    //      the explanation (owner 08-30: no caption text narrating it) - the extras lines render amber so
    //      the part the choice moves is the part that pops. ----
    // STAGED, like the tree it frames (09-13): read from the committed values it hid the secondary column while
    // the primary tree already omitted the extras, so "AFTER APPLY" showed a layout with the extras nowhere.
    public bool ShowPreviewSecondary => ShowExtrasLocation && ExtrasCard.StagedOnSecondary
                                        && ExtrasCard.StagedPlacement != Grog.Core.Models.ExtrasPlacement.WithGame;
    public string FolderPreviewCaption => PolicySummaryText;

    /// <summary>The preview's eyebrow. While the card is staged the tree below is a PROPOSAL, and calling
    /// it "folder preview" in that state is what let the old UI describe a layout the disk never got.</summary>
    public string FolderPreviewEyebrow => ExtrasCard.IsDirty ? "AFTER APPLY" : "FOLDER PREVIEW";

    /// <summary>One preview line; Accent renders it amber (the extras the layout choice moves).</summary>
    public sealed record PreviewLine(string Text, bool Accent);

    public IReadOnlyList<PreviewLine> FolderPreviewPrimary => BuildFolderPreview().primary;
    public IReadOnlyList<PreviewLine> FolderPreviewSecondary => BuildFolderPreview().secondary;

    private (IReadOnlyList<PreviewLine> primary, IReadOnlyList<PreviewLine> secondary) BuildFolderPreview()
    {
        const string game = "Neverwinter Nights 2";
        // No (id) suffix: the sample must FIT the centered inset at 1080 without ellipsis - realism
        // loses to a clipped line (owner-driven centering pass 08-30).
        const string installer = "setup_neverwinter_nights_2.exe";
        // The preview answers "what will Apply do", so it reads the card's STAGED choice, never
        // the manifest. Before 09-11 it read the committed value and could therefore promise a
        // shape the disk did not have and no pending action would give it.
        var layout = ExtrasCard.StagedPlacement;
        bool withGame = layout == Grog.Core.Models.ExtrasPlacement.WithGame;
        bool gameFirst = layout == Grog.Core.Models.ExtrasPlacement.SeparateByGame;
        bool separate = !withGame && ShowExtrasLocation && ExtrasCard.StagedOnSecondary;
        var pri = new List<PreviewLine>();
        var sec = new List<PreviewLine>();

        if (_library.IncludeGames || (withGame && _library.IncludeExtras))
        {
            pri.Add(new("Games/", false));
            pri.Add(new("  " + game + "/", false));
            if (_library.IncludeGames) pri.Add(new("    " + installer, false));
        }
        if (_library.IncludeExtras && withGame)
        {
            // Flat Extras/ inside the game's own folder -- no buckets.
            pri.Add(new("    Extras/", true));
            pri.Add(new("      Soundtrack (FLAC).zip", true));
            pri.Add(new("      Manual (PDF).zip", true));
        }
        else if (_library.IncludeExtras)
        {
            var target = separate ? sec : pri;
            target.Add(new("Extras/", true));
            if (gameFirst)
            {
                target.Add(new("  " + game + "/", true));
                target.Add(new("    Soundtracks/", true));
                target.Add(new("      Soundtrack (FLAC).zip", true));
                target.Add(new("    Manuals/", true));
                target.Add(new("      Manual (PDF).zip", true));
            }
            else
            {
                target.Add(new("  Soundtracks/", true));
                target.Add(new("    " + game + "/", true));
                target.Add(new("      Soundtrack (FLAC).zip", true));
                target.Add(new("  Manuals/", true));
                target.Add(new("    " + game + "/", true));
                target.Add(new("      Manual (PDF).zip", true));
            }
        }

        if (pri.Count == 0) pri.Add(new("(nothing selected)", false));
        if (sec.Count == 0) sec.Add(new("Empty until Primary fills up.", false));
        return (pri, sec);
    }

    /// <summary>One-line what/where/how policy summary shown on the Backups page.</summary>
    public string PolicySummaryText
    {
        get
        {
            var parts = new List<string> { PolicyWhat };
            if (_library.ShowLanguagePolicy) parts.Add(_library.LanguageSummaryShort);
            // WithGame already says where extras live (PolicyHow); adding the WHERE line would read
            // "extras with the games · extras with each game" -- one statement twice.
            if (ShowExtrasLocation && ExtrasSeparateTreeActive) parts.Add(PolicyWhere);
            if (_library.ScopeHasExtras) parts.Add(PolicyHow);
            return string.Join("  ·  ", parts);
        }
    }

    // ---- IExtrasPlacementHost ------------------------------------------------------------------------
    // Implemented EXPLICITLY: the extras card needs these, nothing else does, and an explicit
    // implementation keeps them off the view model's public surface where the views would find them.
    Grog.Core.Manifest.JsonManifestStore? IExtrasPlacementHost.Store => _session.Manifest;
    string IExtrasPlacementHost.BackupRoot => _session.BackupRoot;
    System.Threading.CancellationToken IExtrasPlacementHost.SessionToken => _session.Cts?.Token ?? default;
    System.Collections.Generic.IReadOnlyList<(string? RootId, string? Label)> IExtrasPlacementHost.Locations
        => _root.Storage.BackupLocations.Select(l => ((string?)l.RootId, (string?)l.Label)).ToList();   // CS8619: the interface tuple is nullable
    bool IExtrasPlacementHost.ExtrasInScope => _library.ScopeHasExtras;
    bool IExtrasPlacementHost.MigratePromptOpen => _root.Storage.ShowMigratePrompt;
    void IExtrasPlacementHost.RaiseFolderPreview() => RaiseFolderPreview();
    void IExtrasPlacementHost.AfterCommit() { RaisePolicyState(); _root.Storage.RefreshBackupLocations(); }
    void IExtrasPlacementHost.OfferPlan(Grog.Core.Runs.LayoutMigrationPlan plan) => _root.Storage.OfferExtrasPlacementPlan(plan);
    void IExtrasPlacementHost.LeaveTo(string? view) => _root.NavigateLeaving(view);
    void IExtrasPlacementHost.Log(string message, bool isError) => _root.Log(message, isError);
}
