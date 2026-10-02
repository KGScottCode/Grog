// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System.IO;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Tests.Framework;

/// <summary>The v6 schema contract: a FRESH library defaults to WithGame; a pre-v6 manifest derives its
/// layout from the legacy ExtrasInsideGame bool so an existing library NEVER silently changes shape; and
/// the bool mirror is kept honest on save for downgrade reads.</summary>
[NewBatch]
[Trait("manifest")]
public class ExtrasLayoutSchemaTests
{
    static string TempCfg()
    {
        var baseDir = Directory.CreateTempSubdirectory("grog-schema-").FullName;
        Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", Path.Combine(baseDir, "_cfg"));
        return baseDir;
    }

    static JsonManifestStore StoreAt(string dir) => new(dir);

    [Test] void FreshManifest_DefaultsToWithGame_AndSurvivesRoundTrip()
    {
        var dir = TempCfg();
        try
        {
            var store = StoreAt(dir);
            store.LoadAsync().GetAwaiter().GetResult();
            Assert.Equal(ExtrasPlacement.WithGame, store.Current.ExtrasLayout, "fresh default");
            store.SaveAsync().GetAwaiter().GetResult();

            var again = StoreAt(dir);
            again.LoadAsync().GetAwaiter().GetResult();
            Assert.Equal(ExtrasPlacement.WithGame, again.Current.ExtrasLayout, "reload must NOT re-derive from the bool");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test] void PreV6Manifest_DerivesLayoutFromLegacyBool()
    {
        var dir = TempCfg();
        try
        {
            // A v5-era manifest: no ExtrasLayout field, bool says by-type. Layout must come out SeparateByType.
            File.WriteAllText(Path.Combine(dir, JsonManifestStore.FileName),
                "{ \"SchemaVersion\": 5, \"ExtrasInsideGame\": false }");
            var store = StoreAt(dir);
            store.LoadAsync().GetAwaiter().GetResult();
            Assert.Equal(ExtrasPlacement.SeparateByType, store.Current.ExtrasLayout, "bool false -> by-type");
            Assert.Equal(6, store.Current.SchemaVersion, "migrated to v6");

            // And bool true -> by-game (the pre-v6 default), never WithGame.
            File.WriteAllText(Path.Combine(dir, JsonManifestStore.FileName),
                "{ \"SchemaVersion\": 5, \"ExtrasInsideGame\": true }");
            var store2 = StoreAt(dir);
            store2.LoadAsync().GetAwaiter().GetResult();
            Assert.Equal(ExtrasPlacement.SeparateByGame, store2.Current.ExtrasLayout, "bool true -> by-game, existing libraries keep their shape");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test] void Save_KeepsLegacyBoolMirrorHonest()
    {
        var dir = TempCfg();
        try
        {
            var store = StoreAt(dir);
            store.LoadAsync().GetAwaiter().GetResult();
            store.Current.ExtrasLayout = ExtrasPlacement.SeparateByType;   // raw set, mirror stale on purpose
            store.SaveAsync().GetAwaiter().GetResult();
            var json = File.ReadAllText(Path.Combine(dir, JsonManifestStore.FileName));
            Assert.True(System.Text.RegularExpressions.Regex.IsMatch(json, "\"ExtrasInsideGame\":\\s*false"), "save re-derives the mirror for downgrade reads");
        }
        finally { Directory.Delete(dir, true); }
    }
}
