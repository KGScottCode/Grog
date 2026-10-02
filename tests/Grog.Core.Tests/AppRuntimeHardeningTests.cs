// Grog - GOG.com library backup tool
using System.Linq;
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Grog.App;
using Grog.App.Runtime;
using Grog.Core.Format;
using Grog.Core.Manifest;
using Grog.Core.Tests.Framework;

/// <summary>Pure-static App runtime seams and the Core version trim: failure reporting that must say things
/// once and clear itself when the next write lands.</summary>
[NewBatch]
[Trait("runtime")]
public class AppRuntimeHardeningTests
{
    /// <summary>A store whose save always throws: its root sits under a plain file, so the directory create fails.</summary>
    private static JsonManifestStore BrokenStore(string dir)
    {
        var blocker = Path.Combine(dir, "blocker");
        File.WriteAllText(blocker, "not a directory");
        return new JsonManifestStore(Path.Combine(blocker, "root"));
    }

    private static void WaitUntil(Func<bool> cond, int ms = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!cond() && sw.ElapsedMilliseconds < ms) Thread.Sleep(10);
    }

    [Test] void ManifestSaves_LogsOnce_PerReason_AndResetsWhenASaveLands()
    {
        var dir = Directory.CreateTempSubdirectory("grog-msaves-").FullName;
        var logged = new List<string>();
        var raised = new List<string?>();
        var saves = new ManifestSaves();   // own instance: no other class's in-flight save can touch it
        saves.LogError = s => { lock (logged) logged.Add(s); };
        saves.NotSavedChanged = r => { lock (raised) raised.Add(r); };
        try
        {
            var good = new JsonManifestStore(Path.Combine(dir, "good"));

            var broken = BrokenStore(dir);
            int Mine() { lock (raised) return raised.Count(r => r is not null && r.Contains("blocker")); }
            int Cleared() { lock (raised) { int last = raised.FindLastIndex(r => r is not null && r.Contains("blocker")); return raised.Skip(last + 1).Count(r => r is null); } }
            int Logged() { lock (logged) return logged.Count(l => l.Contains("blocker")); }

            saves.SaveInBackground(broken, "test one");
            WaitUntil(() => Mine() >= 1);
            saves.SaveInBackground(broken, "test two");
            WaitUntil(() => Mine() >= 2);
            Assert.Equal(1, Logged(), "the same reason is logged once");
            Assert.Equal(2, Mine(), "NotSavedChanged fires for every failed save");
            Assert.True(saves.LastError is not null, "LastError holds the reason while saves fail");

            saves.SaveInBackground(good, "test three");
            WaitUntil(() => Cleared() >= 1 && saves.LastError is null);
            Assert.Null(saves.LastError, "a landed save clears LastError");
            Assert.True(Cleared() >= 1, "NotSavedChanged is raised with null when a save lands");

            saves.SaveInBackground(broken, "test four");
            WaitUntil(() => Mine() >= 3);
            Assert.Equal(2, Logged(), "after a reset the same reason is logged again");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Test] void AppSettings_LastSaveError_IsSet_OnUnwritablePath_AndClearedByTheNextGoodSave()
    {
        var dir = Directory.CreateTempSubdirectory("grog-settings-").FullName;
        var oldEnv = Environment.GetEnvironmentVariable("GROG_CONFIG_DIR");
        Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", dir);
        try
        {
            // A directory squatting on the temp path makes the write fail on every platform, root or not.
            var tmpPath = Path.Combine(dir, Grog.Core.Storage.GrogPaths.SettingsFileName + ".tmp");
            Directory.CreateDirectory(tmpPath);
            var s = new AppSettings();
            s.Save();
            Assert.True(AppSettings.LastSaveError is not null, "a failed save records its reason");

            Directory.Delete(tmpPath);
            s.Save();
            Assert.Null(AppSettings.LastSaveError, "the next good save clears it");
            Assert.True(File.Exists(Path.Combine(dir, Grog.Core.Storage.GrogPaths.SettingsFileName)), "and the file landed");
        }
        finally
        {
            Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", oldEnv);
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Test] void VersionText_Trim3_DropsMetadataAndFourthPart()
    {
        Assert.Equal("1.2.3", VersionText.Trim3("1.2.3+abc123", null), "build metadata is dropped");
        Assert.Equal("1.2.3", VersionText.Trim3("1.2.3.4", null), "a fourth part is dropped");
        Assert.Equal("1.2.3", VersionText.Trim3("1.2.3.4+sha", null), "both at once");
        Assert.Equal("1.2.3-beta", VersionText.Trim3("1.2.3-beta", null), "a prerelease tag stays");
        Assert.Equal("9.8.7", VersionText.Trim3("", "9.8.7.6"), "an empty version falls back, trimmed");
        Assert.Equal("0.0.0", VersionText.Trim3(null, null), "nothing at all reads 0.0.0");
    }
}
