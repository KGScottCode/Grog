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

internal sealed class CliContext
{
    public string[] Args { get; }
    public string Command { get; }
    public HttpClient Http { get; }
    public GrogPaths Paths { get; }
    public string RootFlag { get; }
    public bool Silent { get; }
    public bool JsonOut { get; }
    public bool NonInteractive { get; }
    public IInteractiveLoginProvider LoginProvider { get; }
    public JsonManifestStore Manifest { get; }
    public FileTokenStore TokenStore { get; }
    public GogAuthService Auth { get; }
    public GogApiClient Api { get; }
    public AccountSessions Sessions { get; }
    public GrogAccount? FirstAccount { get; }
    public string DefaultTokenPath { get; }

    public CliContext(string[] args, string command, HttpClient http, GrogPaths paths, string rootFlag, bool silent, bool jsonOut,
                      bool nonInteractive, IInteractiveLoginProvider loginProvider, JsonManifestStore manifest,
                      FileTokenStore tokenStore, GogAuthService auth, GogApiClient api, AccountSessions sessions,
                      GrogAccount? firstAccount, string defaultTokenPath)
    {
        Args = args; Command = command; Http = http; Paths = paths; RootFlag = rootFlag; Silent = silent; JsonOut = jsonOut;
        NonInteractive = nonInteractive; LoginProvider = loginProvider; Manifest = manifest;
        TokenStore = tokenStore; Auth = auth; Api = api; Sessions = sessions; FirstAccount = firstAccount; DefaultTokenPath = defaultTokenPath;
    }

    public string ContentRoot()
        => !string.IsNullOrWhiteSpace(RootFlag) ? RootFlag
           : (Manifest.Current?.PrimaryRootId is { } pid
               ? Manifest.Current.Roots.FirstOrDefault(r => r.Id == pid)?.PathHint : null) ?? "";

    private Grog.Core.Volumes.BackupLayout? _layout;
    public Grog.Core.Volumes.BackupLayout Layout()
    {
        if (_layout is null && !string.IsNullOrWhiteSpace(RootFlag) && Manifest.Current?.PrimaryRootId is { } pid
            && Manifest.Current.Roots.FirstOrDefault(r => r.Id == pid) is { } primary)
            primary.PathHint = RootFlag;  
        if (_layout is null)
        {
            var contentRoot = ContentRoot();
            var m = Manifest.Current!;
            using (Manifest.Gate.Enter()) Grog.Core.Volumes.BackupLayout.EnsurePrimary(m, contentRoot);

            _layout = new Grog.Core.Volumes.BackupLayout(m, contentRoot);
        }
        return _layout;
    }

    public void ResetLayout() => _layout = null;
}
