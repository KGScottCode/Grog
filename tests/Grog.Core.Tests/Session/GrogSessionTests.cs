// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests.Session;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Grog.App;
using Grog.App.Runtime;
using Grog.App.ViewModels;
using Grog.Core.Auth;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Storage;
using Grog.Core.Tests.Framework;

/// <summary>The App's session layer (S1, 09-08), driven with a temp profile and no window: the first tests
/// of App-level behaviour that never needed Avalonia. Operations join in S2.</summary>
[Trait("session")]
public class GrogSessionTests
{
    /// <summary>A throwaway profile: GROG_CONFIG_DIR points at it for the test's life.</summary>
    private sealed class Profile : IDisposable
    {
        public string Dir { get; } = Directory.CreateTempSubdirectory("grog-session-").FullName;
        public string ConfigDir => Path.Combine(Dir, "cfg");
        public GrogPaths Paths { get; }
        private readonly string? _envBefore;
        public Profile()
        {
            _envBefore = Environment.GetEnvironmentVariable("GROG_CONFIG_DIR");
            Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", ConfigDir);
            Paths = GrogPaths.Resolve(Path.Combine(Dir, "backup"));
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", _envBefore);
            try { Directory.Delete(Dir, true); } catch { }
        }
    }

    private static GrogSession NewSession(List<(string Msg, bool Err, LogCategory Cat)>? log = null)
    {
        var s = new GrogSession(new AppSettings());
        if (log is not null) s.LogSink = (m, e, c) => log.Add((m, e, c));
        return s;
    }

    [Test] public async Task OpenStore_OnAFreshProfile_GivesAnEmptyLoadedManifest()
    {
        using var p = new Profile();
        var s = NewSession();
        await s.OpenStoreAsync(p.Paths);
        Assert.True(s.Manifest is not null, "store created");
        Assert.Equal(0, s.Manifest!.Current.Items.Count, "empty library");   // asserted non-null on the line above
        Assert.Equal(p.Paths.ConfigDir, s.ConfigDir, "config dir remembered once for the 1 Hz readers");
        Assert.True(ReferenceEquals(s.ConfigPaths, p.Paths), "paths kept");
        Assert.False(s.ServicesReady, "the shell decides readiness, not the store open");
    }

    [Test] public async Task OpenStore_ReloadsWhatWasSaved()
    {
        using var p = new Profile();
        var a = NewSession();
        await a.OpenStoreAsync(p.Paths);
        a.Manifest.Mutate(m => m.Items.Add(new LibraryItem { GogId = 7, Title = "Seven" }));
        await a.Manifest.SaveAsync();

        var b = NewSession();
        await b.OpenStoreAsync(p.Paths);
        Assert.Equal("Seven", b.Manifest.Current.ItemById(7)?.Title, "a second session sees the first's save");
    }

    [Test] public async Task OpenStore_RefusesANewerBuildsProfile()
    {
        using var p = new Profile();
        Directory.CreateDirectory(Path.GetDirectoryName(p.Paths.ManifestPath)!);
        File.WriteAllText(p.Paths.ManifestPath, "{\"SchemaVersion\": " + (LibraryManifest.CurrentSchemaVersion + 1) + "}");
        var s = NewSession();
        bool refused = false;
        try { await s.OpenStoreAsync(p.Paths); } catch (ManifestTooNewException) { refused = true; }
        Assert.True(refused, "the session surfaces the ceiling; the shell shows it");
    }

    [Test] public async Task Rebind_WithNoAccounts_UsesTheScratchTokenFile_ThenTheFirstAccounts()
    {
        using var p = new Profile();
        var s = NewSession();
        await s.OpenStoreAsync(p.Paths);
        s.RebindInteractiveSession(new NonInteractiveLoginProvider());
        Assert.True(s.InteractiveTokenPath.EndsWith("tokens-connect.tmp.json"), "no accounts: a scratch file until Connect learns the name");
        Assert.True(s.Api is not null && s.Auth is not null && s.TokenStore is not null, "clients built on it");

        s.Manifest.Mutate(m => m.Accounts.Add(new GrogAccount { Id = "acct-1", Username = "kevin" }));
        s.RebindInteractiveSession(new NonInteractiveLoginProvider());
        var expected = new AccountService(s.Manifest, p.Paths).TokenPathFor("acct-1");
        Assert.Equal(expected, s.InteractiveTokenPath, "with an account, its own token file backs the session");
    }

    [Test] public async Task SaveSkipped_And_Throttle_ReachTheLogSink()
    {
        using var p = new Profile();
        var log = new List<(string Msg, bool Err, LogCategory Cat)>();
        var s = NewSession(log);
        await s.OpenStoreAsync(p.Paths);
        s.RebindInteractiveSession(new NonInteractiveLoginProvider());
        s.Api.OnThrottle?.Invoke("GOG asked us to wait 3 s");
        Assert.True(log.Exists(l => l.Msg.Contains("wait 3 s") && !l.Err), "throttle waits are informational log lines");
        s.Log("boom", isError: true, category: LogCategory.Download);
        Assert.True(log.Exists(l => l.Msg == "boom" && l.Err && l.Cat == LogCategory.Download), "session Log forwards category and severity");
    }
}
