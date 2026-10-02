// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Net.Http;
using Grog.Core.Auth;
using Grog.Core.Api;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Storage;
using System.Linq;

namespace Grog.Cli;

/// <summary>Everything Program.cs resolves before dispatching a verb: arguments, storage paths, the manifest,
/// the default session and its API client, the per-account session cache, and the lazily built layout.
/// Verbs read from this instead of closing over Program.cs locals (09-02 verb split).</summary>
internal sealed class CliContext
{
    public string[] Args { get; }
    public string Command { get; }
    public HttpClient Http { get; }
    public GrogPaths Paths { get; }
    /// <summary>Explicit --root only (empty when not given -- no env/exe fallback).</summary>
    public string RootFlag { get; }
    public bool Silent { get; }
    public bool JsonOut { get; }
    public bool NonInteractive { get; }
    public bool BrowserFlag { get; }
    public IInteractiveLoginProvider LoginProvider { get; }
    public JsonManifestStore Manifest { get; }
    public FileTokenStore TokenStore { get; }
    public GogAuthService Auth { get; }
    public GogApiClient Api { get; }
    public AccountSessions Sessions { get; }
    public GrogAccount? FirstAccount { get; }
    public string DefaultTokenPath { get; }

    public CliContext(string[] args, string command, HttpClient http, GrogPaths paths, string rootFlag, bool silent, bool jsonOut,
                      bool nonInteractive, bool browserFlag, IInteractiveLoginProvider loginProvider, JsonManifestStore manifest,
                      FileTokenStore tokenStore, GogAuthService auth, GogApiClient api, AccountSessions sessions,
                      GrogAccount? firstAccount, string defaultTokenPath)
    {
        Args = args; Command = command; Http = http; Paths = paths; RootFlag = rootFlag; Silent = silent; JsonOut = jsonOut;
        NonInteractive = nonInteractive; BrowserFlag = browserFlag; LoginProvider = loginProvider; Manifest = manifest;
        TokenStore = tokenStore; Auth = auth; Api = api; Sessions = sessions; FirstAccount = firstAccount; DefaultTokenPath = defaultTokenPath;
    }

    /// <summary>The content root is where downloaded files live. It is NEVER guessed: an explicit --root wins,
    /// otherwise it's the stored primary device's path (set by a prior `backup --primary=...` or the GUI). Empty
    /// when neither exists -- content commands must require one and fail fast rather than operate on a phantom
    /// folder.</summary>
    public string ContentRoot()
        => !string.IsNullOrWhiteSpace(RootFlag) ? RootFlag
           : (Manifest.Current?.PrimaryRootId is { } pid
               ? Manifest.Current.Roots.FirstOrDefault(r => r.Id == pid)?.PathHint : null) ?? "";

    private Grog.Core.Volumes.BackupLayout? _layout;
    /// <summary>Multi-volume layout, created lazily after the manifest is loaded (needs its roots/policy).</summary>
    public Grog.Core.Volumes.BackupLayout Layout()
    {
        if (_layout is null && !string.IsNullOrWhiteSpace(RootFlag) && Manifest.Current?.PrimaryRootId is { } pid
            && Manifest.Current.Roots.FirstOrDefault(r => r.Id == pid) is { } primary)
            primary.PathHint = RootFlag;   // an explicit --root IS the primary's path (the layout used to write it back itself)
        if (_layout is null)
        {
            // First run adds the primary root inside the layout ctor: that structure change goes under the
            // gate (the volume scan it also does is read-only disk probing). (manifest gate 09-08)
            var contentRoot = ContentRoot();
            var m = Manifest.Current!;
            using (Manifest.Gate.Enter()) Grog.Core.Volumes.BackupLayout.EnsurePrimary(m, contentRoot);

            _layout = new Grog.Core.Volumes.BackupLayout(m, contentRoot);
        }
        return _layout;
    }

    /// <summary>Roots changed (backup --primary/--secondary): the next Layout() rebuilds against them.</summary>
    public void ResetLayout() => _layout = null;
}
