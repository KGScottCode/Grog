// Grog - headless screenshot harness (dev tool). Seeds a manifest, renders real screens to PNG.
using System;
using System.IO;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia.LogicalTree;
using System.Linq;
using Grog.App;
using Grog.App.ViewModels;
using Grog.App.Views;
using Grog.Core.Download;
using Grog.Core.Manifest;
using Grog.Core.Models;
using Grog.Core.Storage;
using Grog.Core.Volumes;

// --- 1. temp env
string tmp = Path.Combine(Path.GetTempPath(), "grog-shots-" + Guid.NewGuid().ToString("N")[..8]);
string root = Path.Combine(tmp, "backup");
string cfg = Path.Combine(tmp, "cfg");
Directory.CreateDirectory(root); Directory.CreateDirectory(cfg);
Environment.SetEnvironmentVariable("GROG_BACKUP_ROOT", root);
Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", cfg);
Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", cfg);
string hiddenIdsJson = "[]";
if (Environment.GetEnvironmentVariable("GROG_SHOT_MANIFEST") is { Length: > 0 } mp
    && Path.GetDirectoryName(mp) is { } mdir && File.Exists(Path.Combine(mdir, "app-settings.json")))
{
    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(mdir, "app-settings.json")));
    if (doc.RootElement.TryGetProperty("HiddenIds", out var hid))
        hiddenIdsJson = hid.GetRawText();
}
if (Environment.GetEnvironmentVariable("GROG_SHOT_SETTINGS") is { Length: > 0 } sp && File.Exists(sp))
{
    var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(sp))!.AsObject();
    node["ChosenRoot"] = root;
    File.WriteAllText(Path.Combine(cfg, "app-settings.json"), node.ToJsonString());
    Console.WriteLine($"real settings: {sp}");
}
else
File.WriteAllText(Path.Combine(cfg, "app-settings.json"),
    $"{{\"ChosenRoot\":\"{root.Replace("\\","\\\\")}\",\"IncludeGames\":true,\"IncludeExtras\":true,\"LanguagesChosen\":true,\"Platforms\":[\"windows\",\"linux\"],\"PlatformsChosen\":true,\"HiddenIds\":{hiddenIdsJson}}}");

// --- 2. seed a manifest
static GameFile File2(string key, string name, FileKind kind, FileState st, long size, long? partial = null)
    => new() { FileKey = key, Name = name, Kind = kind, State = st, ExpectedSizeBytes = size,
               Os = kind == FileKind.Extra ? "" : "windows", PartialBytes = partial, HasPartial = partial > 0 };

static GameFile Present(GameFile f, string root, long size, string rel, string extraType = "")
{ f.RootId = root; f.State = FileState.Verified; f.LocalSizeBytes = size; f.LocalRelativePath = rel; f.ExtraType = extraType; return f; }

var _extraSeed = new System.Collections.Generic.List<LibraryItem>();
var alpha = new LibraryItem { GogId = 101, Title = "Neverwinter Nights 2 Complete", Slug = "nwn2", Type = ProductType.Game,
                              DateAcquired = new DateTimeOffset(2019, 11, 27, 18, 4, 0, TimeSpan.Zero) };
alpha.Files.Add(Present(File2("a1", "setup_nwn2_(83148)-1.bin", FileKind.Installer, FileState.Verified, 4_000_000_000), "sec", 4_000_000_000, "nwn2/setup1.bin"));
alpha.Files.Add(File2("a2", "setup_nwn2_(83148)-2.bin", FileKind.Installer, FileState.NotBackedUp, 4_000_000_000, 1_600_000_000)); // paused ~40%
alpha.Files.Add(Present(File2("a2p", "Patch (1.23 to 1.23.1765)", FileKind.Patch, FileState.Verified, 42_000_000), "primary", 42_000_000, "nwn2/patch.exe"));
alpha.Files.Last(f => f.FileKey == "a2p").Version = "1.23.1765";
alpha.Files.Add(Present(File2("a3", "setup_nwn2_(83148)-3.bin", FileKind.Installer, FileState.Verified, 3_900_000_000), "primary", 3_900_000_000, "nwn2/setup3.bin"));
alpha.Files.Add(Present(File2("a4", "Soundtrack (FLAC).zip", FileKind.Extra, FileState.Verified, 640_000_000), "primary", 640_000_000, "nwn2/soundtrack.zip", "soundtrack"));
alpha.Files.Add(Present(File2("a5", "Manual (PDF).zip", FileKind.Extra, FileState.Verified, 12_000_000), "primary", 12_000_000, "nwn2/manual.zip", "manual"));
alpha.Files.Add(Present(File2("a6", "Manual (PDF) [secondary].zip", FileKind.Extra, FileState.Verified, 9_000_000), "sec", 9_000_000, "nwn2/manual2.zip", "manual"));
var beta = new LibraryItem { GogId = 102, Title = "Baldur's Gate 2 Complete", Slug = "bg2", Type = ProductType.Game, IsNew = true };
beta.Files.Add(Present(File2("b1", "setup_baldurs_gate_2_(83210)-1.bin", FileKind.Installer, FileState.Verified, 5_100_000_000), "primary", 5_100_000_000, "bg2/setup1.bin"));
beta.Files.Add(Present(File2("b2", "setup_baldurs_gate_2_(83210)-2.bin", FileKind.Installer, FileState.Verified, 4_400_000_000), "sec", 4_400_000_000, "bg2/setup2.bin"));

if (Environment.GetEnvironmentVariable("GROG_SHOT_GUIDE_STEP") == "back-up")
{
    var tiny = new LibraryItem { GogId = 104, Title = "A Short Hike", Slug = "short-hike", Type = ProductType.Game };
    tiny.Files.Add(File2("t1", "setup_a_short_hike_(64110).exe", FileKind.Installer, FileState.NotBackedUp, 180_000_000));
    beta.Files.Add(File2("b3", "setup_baldurs_gate_2_patch.exe", FileKind.Installer, FileState.NotBackedUp, 90_000_000));
    _extraSeed.Add(tiny);
}

// GROG_SHOT_MANYDONE=1
if (Environment.GetEnvironmentVariable("GROG_SHOT_MANYDONE") == "1")
{
    string[] names = { "Baldur's Gate", "Fallout 2", "Planescape Torment", "Arcanum", "System Shock 2",
                       "Deus Ex", "Thief Gold", "Grim Fandango", "Outcast", "Sacrifice" };
    // Two NOT-STARTED rows, so the resting treatment can be judged beside the completed one.
    foreach (var t in new[] { "Blade Runner", "Little Big Adventure" })
    {
        var ng = new LibraryItem { GogId = 3100 + t.Length, Title = t, Slug = "n" + t.Length, Type = ProductType.Game };
        ng.Files.Add(File2("nf" + t.Length, "setup.bin", FileKind.Installer, FileState.NotBackedUp, 700_000_000));
        _extraSeed.Add(ng);
    }
    for (int n = 0; n < names.Length; n++)
    {
        var g = new LibraryItem { GogId = 2000 + n, Title = names[n], Slug = "g" + n, Type = ProductType.Game };
        for (int f = 0; f < 3; f++)
            g.Files.Add(Present(File2($"d{n}_{f}", $"setup_{n}_{f}.bin", FileKind.Installer, FileState.Verified, 900_000_000),
                                "primary", 900_000_000, $"g{n}/setup{f}.bin"));
        _extraSeed.Add(g);
    }
}

// GROG_SHOT_SCOPEMIX=1
bool scopeMix = Environment.GetEnvironmentVariable("GROG_SHOT_SCOPEMIX") == "1";
if (scopeMix)
{
    var mac = new LibraryItem { GogId = 3001, Title = "Alien Swarm (Mac Edition)", Slug = "alienswarm", Type = ProductType.Game };
    var mf = File2("m1", "setup_mac.pkg", FileKind.Installer, FileState.NotBackedUp, 1_900_000_000); mf.Os = "mac";   // > 1 GB: the scan summary's scope-cost block shows only past that
    mac.Files.Add(mf);
    var nd = new LibraryItem { GogId = 3002, Title = "Blade Runner", Slug = "bladerunner", Type = ProductType.Game };
    nd.Files.Add(File2("n1", "setup_br.exe", FileKind.Installer, FileState.NotBackedUp, 1_400_000_000));
    _extraSeed.Add(mac); _extraSeed.Add(nd);
}

var manifest = new LibraryManifest { PrimaryRootId = "primary" };
if (scopeMix) manifest.Scope = new Grog.Core.Sync.ScopeSettings { Platforms = { "windows" }, PlatformsChosen = true };

// GROG_SE_PROBE=1
if (Environment.GetEnvironmentVariable("GROG_SE_PROBE") == "1" || Environment.GetEnvironmentVariable("GROG_SE_SCOPE") == "1")
    manifest.Scope = new Grog.Core.Sync.ScopeSettings { IncludeGames = true, IncludeExtras = true,
        LanguagesChosen = true, ExtraLanguages = new(), ExtraLanguagesChosen = true, PlatformsChosen = true };

// GROG_SHOT_FRESH=1
bool freshProfile = Environment.GetEnvironmentVariable("GROG_SHOT_FRESH") == "1";

// GROG_SHOT_MANIFEST=<path>
if (Environment.GetEnvironmentVariable("GROG_SHOT_MANIFEST") is { Length: > 0 } realPath && !freshProfile)
{
    var real = System.Text.Json.JsonSerializer.Deserialize<LibraryManifest>(
        System.IO.File.ReadAllText(realPath),
        new System.Text.Json.JsonSerializerOptions
        {
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
        }) ?? throw new InvalidOperationException($"could not read manifest: {realPath}");
    foreach (var it in real.Items) manifest.Items.Add(it);
    manifest.Scope = real.Scope;
    Console.WriteLine($"real manifest: {real.Items.Count} items, {real.Items.Sum(i => i.Files.Count)} files");
}
else if (!freshProfile) { manifest.Items.Add(alpha); manifest.Items.Add(beta); }
if (!freshProfile && Environment.GetEnvironmentVariable("GROG_SHOT_MANIFEST") is null)
    foreach (var x in _extraSeed) manifest.Items.Add(x);

// GROG_SHOT_STRESS=N
int stressN = int.TryParse(Environment.GetEnvironmentVariable("GROG_SHOT_STRESS"), out var _sn) ? _sn : 0;
if (stressN > 0 && !freshProfile)
{
    var rnd = new Random(1234);
    for (int g = 0; g < stressN; g++)
    {
        var it = new LibraryItem { GogId = 100000 + g, Title = $"Stress Game {g:D4} - The Directors Cut Edition", Slug = $"stress_game_{g:D4}", Type = ProductType.Game };
        for (int f = 0; f < 4; f++)
        {
            FileState st = ((g + f) % 5) switch { 0 => FileState.Verified, 1 => FileState.Present, 2 => FileState.NotBackedUp, 3 => FileState.Outdated, _ => FileState.Verified };
            var gf = File2($"s{g}i{f}", $"setup_stress_{g:D4}_({f}).bin", FileKind.Installer, st, 200_000_000L + rnd.Next(1_500_000_000));
            // "sec" is the seed's secondary id; "secondary" matched no root, so half the stress files showed nowhere.
            if (st is FileState.Verified or FileState.Present or FileState.Outdated) { gf.RootId = g % 2 == 0 ? "primary" : "sec"; gf.LocalSizeBytes = gf.ExpectedSizeBytes; gf.LocalRelativePath = $"{it.Slug}/{gf.Name}"; }
            gf.OwnerIds.Add("acc1");
            it.Files.Add(gf);
        }
        for (int f = 0; f < 3; f++)
        {
            FileState st = ((g + f) % 3) == 0 ? FileState.NotBackedUp : FileState.Verified;
            var gf = File2($"s{g}e{f}", $"extra_{g:D4}_{f}.zip", FileKind.Extra, st, 5_000_000L + rnd.Next(300_000_000));
            gf.ExtraType = f switch { 0 => "soundtrack", 1 => "manuals", _ => "wallpapers" };
            if (st == FileState.Verified) { gf.RootId = "primary"; gf.LocalSizeBytes = gf.ExpectedSizeBytes; gf.LocalRelativePath = $"{it.Slug}/extras/{gf.Name}"; }
            gf.OwnerIds.Add("acc1");
            it.Files.Add(gf);
        }
        manifest.Items.Add(it);
    }
    // QUEUE THE GAPS TOO
    int queued = 0;
    foreach (var it in manifest.Items)
        foreach (var gf in it.Files)
            if (gf.State is FileState.NotBackedUp or FileState.Outdated) { manifest.Downloads.Enqueue(it.GogId, gf.FileKey); queued++; }
    Console.WriteLine($"stress seed: {stressN} games, {manifest.Items.Sum(i => i.Files.Count)} files, {queued} queued");
}
// GROG_SHOT_SCOPEFULL=1
if (Environment.GetEnvironmentVariable("GROG_SHOT_SCOPEFULL") == "1" && !freshProfile)
{
    var oses = new[] { "windows", "mac", "linux" };
    var langs = new[] { "English", "Polish", "German", "French", "Spanish", "Italian", "Russian", "Czech" };
    for (int g = 0; g < 12; g++)
    {
        var it = new LibraryItem { GogId = 200000 + g, Title = $"Scope Sample {g:D2}", Slug = $"scope_sample_{g:D2}", Type = ProductType.Game };
        for (int f = 0; f < 6; f++)
        {
            var gf = File2($"sc{g}i{f}", $"setup_scope_{g:D2}_({f}).exe", FileKind.Installer, FileState.Verified, 900_000_000L + f * 40_000_000L);
            gf.Os = oses[f % 3];
            gf.Language = langs[(g + f) % langs.Length];
            gf.RootId = "primary"; gf.LocalSizeBytes = gf.ExpectedSizeBytes; gf.LocalRelativePath = $"{it.Slug}/{gf.Name}";
            gf.OwnerIds.Add("acc1");
            it.Files.Add(gf);
        }
        for (int f = 0; f < 2; f++)
        {
            var gf = File2($"sc{g}e{f}", f == 0 ? $"soundtrack_{g:D2} (FLAC).zip" : $"Polish localization {g:D2}.zip",
                           FileKind.Extra, FileState.Verified, 60_000_000L + f * 9_000_000L);
            gf.ExtraType = f == 0 ? "soundtrack" : "localization";
            gf.RootId = "primary"; gf.LocalSizeBytes = gf.ExpectedSizeBytes; gf.LocalRelativePath = $"{it.Slug}/extras/{gf.Name}";
            gf.OwnerIds.Add("acc1");
            it.Files.Add(gf);
        }
        manifest.Items.Add(it);
    }
    manifest.Scope ??= new Grog.Core.Sync.ScopeSettings();
    manifest.Scope.Languages = new System.Collections.Generic.List<string> { "English", "Polish" };
    Console.WriteLine("scopefull seed: 3 platforms, 8 languages");
}

// --- SHOT SEED
bool batch = Environment.GetEnvironmentVariable("GROG_SHOT_BATCH") == "1";
int issues = int.TryParse(Environment.GetEnvironmentVariable("GROG_SHOT_ISSUES"), out var _n) ? _n : 0;
if (batch && issues == 0) issues = 3;
if (issues > 0)
{
    var bad = new LibraryItem { GogId = 103, Title = "Icewind Dale Enhanced Edition", Slug = "iwd", Type = ProductType.Game };
    // GROG_SHOT_GONEONLY=1
    bool goneOnly = Environment.GetEnvironmentVariable("GROG_SHOT_GONEONLY") == "1";
    for (int i = 0; i < issues; i++)
    {
        if (!goneOnly)
        {
            var c = File2($"c{i}", $"setup_iwd_(8322{i})-{i}.bin", FileKind.Installer, FileState.Corrupt, 2_000_000_000);
            c.RootId = "primary"; c.LocalSizeBytes = 2_000_000_000; c.LocalRelativePath = $"iwd/setup{i}.bin";
            bad.Files.Add(c);
        }
        var g = File2($"g{i}", $"Soundtrack track {i} (FLAC).zip", FileKind.Extra, FileState.Missing, 300_000_000);
        g.RootId = "primary"; g.LocalSizeBytes = 300_000_000; g.LocalRelativePath = $"iwd/track{i}.zip"; g.ExtraType = "soundtrack";
        bad.Files.Add(g);
    }
    manifest.Items.Add(bad);
}

// GROG_SHOT_ORPHAN=1
if (Environment.GetEnvironmentVariable("GROG_SHOT_ORPHAN") == "1")
{
    manifest.Accounts.Add(new Grog.Core.Models.GrogAccount { Id = "primaryacct", Username = "Primary" });
    var orph = new LibraryItem { GogId = 990, Title = "Bloody Hell (orphan seed)", Slug = "orphan", Type = ProductType.Game };
    var of = File2("of1", "setup_orphan.exe", FileKind.Installer, FileState.NotBackedUp, 70_000_000);
    of.OwnerIds.Add("ghostaccount");
    orph.Files.Add(of);
    manifest.Items.Add(orph);
    var del = new LibraryItem { GogId = 991, Title = "Truly Delisted (seed)", Slug = "delseed", Type = ProductType.Game, IsDelisted = true };
    var df = File2("df1", "setup_delisted.exe", FileKind.Installer, FileState.NotBackedUp, 70_000_000);
    df.OwnerIds.Add("primaryacct");
    del.Files.Add(df);
    manifest.Items.Add(del);
}

if (batch)
{
    manifest.Accounts.Add(new GrogAccount { Id = "acc1", Username = "GOGUSER", Login = "GogUser@emailaddr.com" });
    manifest.Accounts.Add(new GrogAccount { Id = "acc2", Username = "SECONDBOX", Login = "second@example.com" });
    alpha.HasCloudSaves = true; alpha.CloudClientId = "50225266424";
    alpha.CloudByAccount.Add(new CloudAccountSave { AccountId = "acc1", SizeBytes = 34_500_000, Files = 12, UpdatedUtc = DateTimeOffset.UtcNow.AddDays(-2), SpaceId = "s1" });
    alpha.CloudByAccount.Add(new CloudAccountSave { AccountId = "acc2", SizeBytes = 2_100_000, Files = 3, UpdatedUtc = DateTimeOffset.UtcNow.AddDays(-40), SpaceId = "s2" });
    beta.HasCloudSaves = true; beta.CloudClientId = "50225266425";
    beta.CloudByAccount.Add(new CloudAccountSave { AccountId = "acc1", SizeBytes = 900_000, Files = 2, UpdatedUtc = DateTimeOffset.UtcNow.AddDays(-7), SpaceId = "s3" });
    beta.Files.Add(File2("b3", "setup_baldurs_gate_2_patch.exe", FileKind.Patch, FileState.NotBackedUp, 90_000_000));
    beta.Files.Add(File2("b4", "setup_baldurs_gate_2_(83210)-3.bin", FileKind.Installer, FileState.NotBackedUp, 1_200_000_000));
}

manifest.Downloads.Enqueue(101, "a2");        // the paused file is queued
if (batch) { manifest.Downloads.Enqueue(102, "b3"); manifest.Downloads.Enqueue(102, "b4"); }
manifest.Downloads.Paused = true;             // queue is paused
var store = new JsonManifestStore(GrogPaths.Resolve(root));
await store.LoadAsync();                        // creates default roots
store.Current.Items.AddRange(manifest.Items);
store.Current.Downloads = manifest.Downloads;
store.Current.Accounts.AddRange(manifest.Accounts);
store.Current.Scope = manifest.Scope;   // carry the seeded scope; dropping it re-seeds from AppSettings
store.Current.Routing = manifest.Routing;   // carry the seeded game pins
store.Current.Roots.Clear();
store.Current.Roots.Add(new BackupRoot { Id = "primary", Label = "Primary", PathHint = root, State = RootState.Online });
if (Environment.GetEnvironmentVariable("GROG_SHOT_NOSEC") != "1")
    store.Current.Roots.Add(new BackupRoot { Id = "sec", Label = Environment.GetEnvironmentVariable("GROG_SHOT_NAMED") == "1" ? "Old laptop drive" : "Secondary", PathHint = "E:/GameBackup", State = RootState.Offline, Removable = true });   // NAMED: the user's name on the heading row
store.Current.PrimaryRootId = "primary";
await store.SaveAsync();

var seedLayout = new BackupLayout(store.Current, root);
foreach (var it in store.Current.Items)
    foreach (var f in it.Files)
    {
        if (f.RootId != "primary" || f.State != FileState.Verified) continue;
        var p = seedLayout.ResolvePath(f);
        if (string.IsNullOrEmpty(p)) continue;
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        using var fs = new FileStream(p, FileMode.Create);
        fs.SetLength(f.LocalSizeBytes ?? f.ExpectedSizeBytes ?? 0);   // sparse: no real disk used
    }

// 3. headless render
AppBuilder.Configure<App>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .UseSkia().WithInterFont().SetupWithoutStarting();

void Pump() { for (int i = 0; i < 6; i++) { Dispatcher.UIThread.RunJobs(); System.Threading.Thread.Sleep(120); } }
string shotDir = Environment.GetEnvironmentVariable("GROG_SHOT_DIR") ?? "";
if (shotDir.Length == 0)
{
    var sd = new DirectoryInfo(AppContext.BaseDirectory);
    while (sd is not null && !File.Exists(Path.Combine(sd.FullName, "Grog.slnx"))) sd = sd.Parent;
    shotDir = sd is null ? "/root/grog" : Path.Combine(sd.FullName, "_shots");
}
Directory.CreateDirectory(shotDir);
void Shot(string name, Avalonia.Controls.Window w)
{ Pump(); var f = w.CaptureRenderedFrame(); f?.Save(Path.Combine(shotDir, $"shot_{name}.png")); Console.WriteLine($"{name}: {(f is null ? "NULL" : $"{f.PixelSize.Width}x{f.PixelSize.Height}")}"); }

Dispatcher.UIThread.Invoke(() =>
{
    var vm = new MainWindowViewModel();
    // 1920x1080 ON PURPOSE
    double shotW = 1920, shotH = 1080;
    if (Environment.GetEnvironmentVariable("GROG_SHOT_BATCH") == "1") { shotW = 1366; shotH = 768; }
    if (Environment.GetEnvironmentVariable("GROG_SHOT_SIZE") is { Length: > 2 } sz)
    {
        var parts = sz.Split('x', 'X');
        if (parts.Length == 2 && double.TryParse(parts[0], out var pw) && double.TryParse(parts[1], out var ph))
            { shotW = pw; shotH = ph; }
    }
    var win = new MainWindow { DataContext = vm, Width = shotW, Height = shotH };
    win.Show();
    for (int i = 0; i < 25; i++) { Dispatcher.UIThread.RunJobs(); System.Threading.Thread.Sleep(150); }
    vm.IsConnected = true;   // fake a connected account so the Library grid renders (no network)
    for (int i = 0; i < 6; i++) { Dispatcher.UIThread.RunJobs(); System.Threading.Thread.Sleep(120); }
    if (vm.Storage.ShowMigratePrompt) vm.Storage.SkipMigrateCommand.Execute(null);   // clear the reorg modal so the Library is unobstructed
    for (int i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); System.Threading.Thread.Sleep(120); }
    Console.WriteLine($"games={vm.Library.Games.Count} connected={vm.IsConnected}");

    // GROG_SHOT_STRESS=N
    if (stressN > 0 && Environment.GetEnvironmentVariable("GROG_SHOT_STRESS_SEEDONLY") != "1")   // SEEDONLY
    {
        Grog.Core.UiThreadGuard.IsUiThread = () => Dispatcher.UIThread.CheckAccess();
        Grog.Core.UiThreadGuard.Violation = what => Console.WriteLine($"GUARD UI thread ran {what}");
        var timings = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<double>>();
        Grog.App.Services.UiWatch.OnTimed = (name, ms) => { if (!timings.TryGetValue(name, out var l)) timings[name] = l = new(); l.Add(ms); };
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        void Call(string method)
        {
            object target = method == "RebuildRows" ? vm.Library : method == "RaiseHealth" ? vm.Overview : method == "RebuildInventory" ? vm.Storage : vm;
            target.GetType().GetMethod(method, flags)!.Invoke(target, null);
        }
        foreach (var page in new[] { "Overview", "Library", "Drives" })
        {
            vm.NavigateCommand.Execute(page); Settle(6);
            timings.Clear();
            var wall = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < 20; i++)
            {
                Call("RaiseDashboard");          // the 1 Hz tick during a run
                Call("RaiseHealth");
                Call("RecomputeActivity");
                Dispatcher.UIThread.RunJobs();
            }
            for (int i = 0; i < 5; i++) { Call("RebuildRows"); Dispatcher.UIThread.RunJobs(); }
            if (page == "Drives") for (int i = 0; i < 5; i++) { Call("RebuildInventory"); Dispatcher.UIThread.RunJobs(); }
            Console.WriteLine($"STRESS {page}: {stressN} games, wall {wall.ElapsedMilliseconds} ms");
            foreach (var kv in timings.OrderByDescending(k => k.Value.Average()))
            {
                var l = kv.Value; l.Sort();
                Console.WriteLine($"  {kv.Key,-28} n={l.Count,3}  avg {l.Average(),7:F1} ms  p50 {l[l.Count / 2],7:F1}  max {l[^1],7:F1}");
            }
        }
        // The stress pass above times the RENDER passes
        {
            vm.NavigateCommand.Execute("Overview"); Settle(6);
            int queued = vm.Overview.DownloadQueueCount;
            void Time(string label, Action a)
            {
                timings.Clear();
                var w = System.Diagnostics.Stopwatch.StartNew();
                a(); Dispatcher.UIThread.RunJobs();
                w.Stop();
                var worst = timings.OrderByDescending(k => k.Value.Sum()).Take(3)
                                   .Select(k => $"{k.Key} n={k.Value.Count} tot {k.Value.Sum():F0} ms");
                Console.WriteLine($"QUEUEOP {label,-22} queued={queued,5}  wall {w.ElapsedMilliseconds,6} ms   {string.Join(" | ", worst)}");
            }
            foreach (var col in new[] { "Size", "Kind", "Name", "Game", "Storage" })
                Time($"sort {col}", () => vm.Overview.SortQueueCommand.Execute(col));
            Time("scope games-only", () => vm.Library.SetScopeCommand.Execute("games"));
            Time("scope both", () => vm.Library.SetScopeCommand.Execute(null));
            Time("clear queue", () => vm.CancelDownloadsCommand.Execute(null));
            Console.WriteLine($"QUEUEOP after clear: queued={vm.Overview.DownloadQueueCount}");
        }
        Grog.App.Services.UiWatch.OnTimed = null;
        return;
    }

    void Settle(int n = 4) { for (int i = 0; i < n; i++) { Dispatcher.UIThread.RunJobs(); System.Threading.Thread.Sleep(120); } }
    void ApplyTheme(Grog.App.GrogTheme t)
    {
        Grog.App.ThemeService.Apply(t);
        foreach (var c in vm.Library.SoftwareChips) c.RaiseColors();
        foreach (var c in vm.Library.ExtraFilterChips) c.RaiseColors();
    }
    void CloseOverlays()
    {
        vm.Library.ShowCompositionModal = false; vm.Storage.ShowDeleteDeviceDialog = false;
        vm.ShowWelcomeOverlay = false; vm.Storage.ShowMigratePrompt = false; vm.Storage.ShowLostDeviceDialog = false;
        vm.Guide.GuideActive = false;
    }
    void AllChipsOn() { foreach (var c in vm.Library.SoftwareChips) c.IsActive = true; foreach (var c in vm.Library.ExtraFilterChips) c.IsActive = true; }
    void SeedMove()
    {
        var job = new Grog.Core.Volumes.ReorgJob { Reason = Grog.Core.Volumes.ReorgReason.ChangeFolder };
        long[] sizes = { 1_200_000_000, 34_000_000, 4_100_000_000, 660_000_000, 86_000_000 };
        string[] titles = { "Baldur's Gate 2 Complete", "Icewind Dale Enhanced Edition", "Neverwinter Nights 2 Complete", "Baldur's Gate 2 Complete", "Icewind Dale Enhanced Edition" };
        for (int i = 0; i < sizes.Length; i++)
            job.Moves.Add(new Grog.Core.Volumes.ReorgMove
            {
                GogId = 1000 + i, FileKey = $"shot/move{i}", Title = titles[i], SizeBytes = sizes[i],
                FromRel = $"Games/{titles[i]}/setup_part{i + 1}.bin", ToRel = $"Games/{titles[i]}/setup_part{i + 1}.bin",
                Kind = Grog.Core.Volumes.ReorgMoveKind.CopyVerifyDelete,
                State = i == 0 ? Grog.Core.Volumes.ReorgMoveState.Copied : Grog.Core.Volumes.ReorgMoveState.Pending,
            });
        job.Current = job.Moves[0];
        vm.Storage.SeedMoveForShots(job, 0.42);
    }


    // GROG_SHOT_BATCH=1
    if (Environment.GetEnvironmentVariable("GROG_SHOT_BATCH") == "1")
    {
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        void Raise(string prop) => typeof(LibraryViewModel)
            .GetMethod("OnPropertyChanged", flags, null, new[] { typeof(string) }, null)!
            .Invoke(vm.Library, new object?[] { prop });
        void B(string name) { Settle(8); Shot("sweep_" + name, win); }

        CloseOverlays(); vm.Library.SelectedRow = null; Settle(6);
        foreach (var sv in win.GetVisualDescendants().OfType<Avalonia.Controls.ScrollViewer>())
            if (sv.Extent.Height > 100 && sv.Viewport.Height > 100 && sv.IsEffectivelyVisible)
                Console.WriteLine($"OVERVIEW extent={sv.Extent.Height:F0} viewport={sv.Viewport.Height:F0} overflow={sv.Extent.Height - sv.Viewport.Height:F0}");
        Shot("sweep_overview", win);

        // GROG_SHOT_MOVE=1 alone renders it too
        SeedMove(); Settle(6);
        Shot("sweep_overview_moving", win);
        vm.Overview.GoneExpanded = true; vm.Overview.CorruptExpanded = true; Settle(4);
        Shot("sweep_overview_expanded", win);
        vm.Overview.GoneExpanded = false; vm.Overview.CorruptExpanded = false;
        vm.Overview.OpenIssuesCommand.Execute(null); Settle(4);
        Shot("sweep_overview_issues_modal", win);
        vm.Overview.ShowIssuesModal = false;

        // Library: chip row, populated (paused) queue, then a selected row's detail pane.
        CloseOverlays(); AllChipsOn(); vm.NavigateCommand.Execute("Library"); B("library");
        vm.Library.SelectedRow = vm.Library.Games.FirstOrDefault(); B("library_detail");
        vm.Library.SelectedRow = null;

        // Library with the scan panel running (the seeded mid-scan mock).
        vm.Library.SeedScanProgress(342, 1206, "Fetching The Witcher 3: Wild Hunt - Game of the Year Edition");
        B("library_scanning");

        // Scan summary modal, with the BY ACCOUNT block a multi-account run leaves behind.
        var passes = new System.Collections.Generic.List<Grog.Core.Sync.AccountSyncPass>
        {
            new("acc1", "GOGUSER", false, null, new Grog.Core.Sync.SyncResult { GamesSeen = 158 }, null),
            new("acc2", "SECONDBOX", true, "signed out", null, null),
        };
        typeof(LibraryViewModel).GetField("_lastScanPasses", flags)!.SetValue(vm.Library, passes);
        foreach (var pn in new[] { "ShowScanAccountsLine", "ScanAccountsLine", "ScanSeenCount",
                                   "ScanNoFilesCount", "ScanErrorCount", "ScanHadErrors" }) Raise(pn);
        vm.Library.ShowCompositionModal = true; B("scan_summary");
        vm.Library.ShowCompositionModal = false;

        // Rescan of a library that already had items and found 3 new games: the "since your last scan"
        // line and the Back Up New Now button.
        typeof(LibraryViewModel).GetField("_itemsBeforeScan", flags)!.SetValue(vm.Library, 155);
        typeof(LibraryViewModel).GetField("_lastNewGameIds", flags)!.SetValue(vm.Library, new System.Collections.Generic.List<long> { 1, 2, 3 });
        typeof(LibraryViewModel).GetField("_lastNewFiles", flags)!.SetValue(vm.Library, 11);
        foreach (var pn in new[] { "ShowScanNewGames", "ScanNewGamesLine" }) Raise(pn);
        vm.Library.ShowCompositionModal = true; B("scan_summary_new");
        vm.Library.ShowCompositionModal = false;
        typeof(LibraryViewModel).GetField("_itemsBeforeScan", flags)!.SetValue(vm.Library, 0);
        typeof(LibraryViewModel).GetField("_lastNewGameIds", flags)!.SetValue(vm.Library, new System.Collections.Generic.List<long>());
        typeof(LibraryViewModel).GetField("_lastNewFiles", flags)!.SetValue(vm.Library, 0);
        foreach (var pn in new[] { "ShowScanNewGames" }) Raise(pn);

        // Accounts BEFORE the cloud shots
        CloseOverlays(); vm.NavigateCommand.Execute("Accounts"); B("accounts");
        // Folders, then Cloud Saves grouped
        vm.NavigateCommand.Execute("Drives"); B("folders");
        vm.NavigateCommand.Execute("CloudSaves"); B("cloud_grouped");
        if (vm.Session.Manifest is JsonManifestStore ms)
        {
            ms.Current.Accounts.RemoveAll(a => a.Id == "acc2");
            foreach (var it in ms.Current.Items) it.CloudByAccount.RemoveAll(e => e.AccountId == "acc2");
            vm.CloudVm.RefreshFromManifest();
        }
        B("cloud_flat");

        // Settings, About.
        vm.NavigateCommand.Execute("Settings"); B("settings");
        vm.NavigateCommand.Execute("About"); B("about");
        Console.WriteLine($"SWEEP done -> {shotDir}");
        return;
    }
    if (Environment.GetEnvironmentVariable("GROG_SHOT_CLOUD") == "1") {
        CloseOverlays(); vm.Library.SelectedRow = null; vm.NavigateCommand.Execute("CloudSaves"); Settle(8);
        Shot("cloud", win); return;
    }
    if (Environment.GetEnvironmentVariable("GROG_SE_PROBE") == "1") {
        CloseOverlays(); Settle(6);
        var mf=vm.Session.Manifest;
        var sc=mf?.Current.Scope;
        Console.WriteLine($"SE manifest.Scope null={sc is null} plats={sc?.Platforms.Count} chosen={sc?.PlatformsChosen} langs={sc?.Languages.Count}");
        var f=typeof(LibraryViewModel);   // (S3.2) the scope fields are the Library page's
        foreach (var n in new[]{"_includeGames","_includeExtras","_languages","_extraLanguages","_platforms"})
        {
            var fi=f.GetField(n, System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
            var v=fi?.GetValue(vm.Library);
            Console.WriteLine($"SE {n}={(v is System.Collections.ICollection c ? "count:"+c.Count : v?.ToString() ?? "null")}");
        }
        return;
    }
    if (Environment.GetEnvironmentVariable("GROG_RAIL_PROBE") == "1") {
        CloseOverlays(); Settle(6);
        foreach (var tb in win.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>())
        {
            var t = tb.Text ?? "";
            if (t.EndsWith("games") || t.EndsWith("files") || t.EndsWith("file"))
            {
                var tl = tb.TranslatePoint(new Avalonia.Point(tb.Bounds.Width, 0), win);
                Console.WriteLine($"PROBE text='{t}' right={tl?.X:F1}");
            }
        }
        foreach (var b in win.GetVisualDescendants().OfType<Avalonia.Controls.Border>())
            if (b.Child is Avalonia.Controls.TextBlock tb2 && (tb2.Text ?? "").EndsWith("file", StringComparison.OrdinalIgnoreCase) || b.Child is Avalonia.Controls.TextBlock tb3 && (tb3.Text ?? "").EndsWith("files"))
            {
                var tl = b.TranslatePoint(new Avalonia.Point(b.Bounds.Width, 0), win);
                Console.WriteLine($"PROBE pillborder right={tl?.X:F1}");
            }
        foreach (var ic in win.GetVisualDescendants().OfType<Avalonia.Controls.ItemsControl>())
            if (ic.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Any())
            {
                var tl = ic.TranslatePoint(new Avalonia.Point(ic.Bounds.Width, 0), win);
                Console.WriteLine($"PROBE dots type={ic.Parent?.GetType().Name} right={tl?.X:F1} w={ic.Bounds.Width:F0}");
            }
        return;
    }
    if (Environment.GetEnvironmentVariable("GROG_STATS_PROBE") == "1") {
        // Run-stats row (ELAPSED / SPEED / FILES LEFT / EST. TIME LEFT)
        CloseOverlays(); vm.Library.SelectedRow = null; Settle(6);
        foreach (var tb in win.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>())
            if (tb.FontSize == 20 && tb.Parent is Avalonia.Controls.StackPanel sp)
            {
                tb.Text = tb.Text == "--" ? "43.99 MB/s" : tb.Text;   // worst-case width in the widest format
                tb.Measure(new Avalonia.Size(double.PositiveInfinity, double.PositiveInfinity));
                Console.WriteLine($"PROBE stat '{tb.Text}' col={sp.Bounds.Width:F0} need={tb.DesiredSize.Width:F0} fits={tb.DesiredSize.Width <= sp.Bounds.Width}");
            }
        return;
    }
    if (Environment.GetEnvironmentVariable("GROG_SHOT_SLOWDRIVE") == "1") {
        // "Waiting for X to respond…": hold the primary's volume busy and let the half-second tick see it.
        CloseOverlays(); vm.Library.SelectedRow = null;
        var primary = vm.Storage.PrimaryLocation?.Path ?? "/";
        var check = vm.Storage.GetType().GetMethod("CheckSlowDrives", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        using (Grog.Core.Storage.DriveResolver.HoldBusyForShots(primary))
        {
            System.Threading.Thread.Sleep(1100); check.Invoke(vm.Storage, null); Settle(2);
            Console.WriteLine($"SLOWDRIVE text='{vm.Storage.WaitingDriveText}' primary='{vm.Storage.PrimaryWaitingText}' rail='{vm.Storage.RailStorageSub}'");
            vm.NavigateCommand.Execute("Overview"); Settle(4); Shot("slowdrive_overview", win);
            vm.NavigateCommand.Execute("Drives"); Settle(4); Shot("slowdrive_storage", win);
        }
        return;
    }
    if (Environment.GetEnvironmentVariable("GROG_SHOT_OOSCOPE") == "1") {
        // A game whose installers are all outside a Linux-only scope
        CloseOverlays(); vm.Library.SelectedRow = null; vm.NavigateCommand.Execute("Library"); Settle(4);
        var lt = vm.Library.GetType(); var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var pf = lt.GetField("_platforms", bf)!;
        GameRowViewModel? pick = null;
        foreach (var g in vm.Library.Games.ToList())
            if (g.Item is { } it && it.Files.Any(f => f.Kind == Grog.Core.Models.FileKind.Extra)
                && !it.Files.Any(f => f.Kind != Grog.Core.Models.FileKind.Extra && string.Equals(f.Os, "linux", StringComparison.OrdinalIgnoreCase))
                && it.Files.Any(f => f.Kind != Grog.Core.Models.FileKind.Extra)) { pick = g; break; }
        Console.WriteLine($"OOSCOPE pick={pick?.Title} platforms field={pf.FieldType.Name}");
        vm.Library.SelectedRow = pick; Settle(4); Shot("ooscope_before", win);
        pf.SetValue(vm.Library, new List<string> { "linux" });
        vm.Library.SelectedRow = null; Settle(2); vm.Library.SelectedRow = pick; Settle(6);
        Console.WriteLine($"OOSCOPE has={vm.Library.HasOutOfScopeInstallers} os={string.Join(",", vm.Library.DetailOutOfScopeOs.Select(r => r.OsBadge))} extras={vm.Library.DetailExtraFiles.Count}");
        Shot("ooscope_after", win);
        return;
    }
    if (Environment.GetEnvironmentVariable("GROG_SHOT_DRIVEWAIT") == "1") {
        // The storage-went-away card, both states
        CloseOverlays(); vm.Library.SelectedRow = null;
        var sv = vm.Storage; var t = sv.GetType(); var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var m = vm.Session.Manifest!.Current;
        t.GetField("_driveGoneRootId", bf)!.SetValue(sv, m.Roots.FirstOrDefault(r => r.Id != m.PrimaryRootId)?.Id ?? m.PrimaryRootId);
        vm.NavigateCommand.Execute("Overview"); Settle(2); sv.RaiseDriveWait(); Settle(3);
        Console.WriteLine($"DRIVEWAIT show={sv.ShowDriveWaitCard} title='{sv.DriveWaitTitle}'");
        Shot("drivewait_gone", win);
        vm.NavigateCommand.Execute("Library"); Settle(2);
        Console.WriteLine($"DRIVEWAIT library show={sv.ShowDriveWaitCard}");
        return;
    }
    if (Environment.GetEnvironmentVariable("GROG_SHOT_MOVE") == "1") {
        CloseOverlays(); vm.Library.SelectedRow = null; SeedMove(); Settle(6);
        Shot("overview_moving", win);
        Console.WriteLine("MOVE tail before: " + string.Join(", ", vm.Storage.ReorgTail.Select(r => r.SizeText)));
        vm.Storage.SortMovesCommand.Execute("Size"); Settle(4);
        Console.WriteLine("MOVE tail sorted: " + string.Join(", ", vm.Storage.ReorgTail.Select(r => r.SizeText)) + " active=" + vm.Storage.ReorgActive?.SizeText);
        Shot("overview_moving_sorted", win); return;
    }
    if (Environment.GetEnvironmentVariable("GROG_SHOT_WONTFIT") == "1") {
        CloseOverlays(); vm.Library.SelectedRow = null; vm.NavigateCommand.Execute("Overview"); Settle(8);
        if (vm.Session.Manifest is JsonManifestStore wfStore)
        {
            int added = 0;
            foreach (var it in wfStore.Current.Items)
                foreach (var gf in it.Files)
                    if (added < 10 && wfStore.Current.Downloads.Enqueue(it.GogId, gf.FileKey, "primary", fits: added is not (1 or 2 or 5))) added++;
            vm.RecomputeActivity(); Settle(6);
            Console.WriteLine($"WONTFIT: added={added} queue={wfStore.Current.Downloads.Snapshot().Count} tail={vm.Overview.DownloadTail.Count} red={vm.Overview.DownloadTail.OfType<QueueRow>().Count(r => r.WontFit)} divider={vm.Overview.DownloadTail.IndexOf(vm.Overview.WontFitDivider)} dividerText={vm.Overview.WontFitDivider.Text} pillRed={vm.Overview.DownloadPill?.WontFit}");
        }
        else Console.WriteLine("WONTFIT: session manifest is not a JsonManifestStore");
        vm.QueueHasUnplaceable = true;
        vm.QueueUnplaceableLine = Grog.App.ViewModels.BackupLocationRow.FitsNowhereLine(398, 377_070_000_000L);
        if (vm.Overview.DownloadTail.Count > 1 && vm.Overview.DownloadTail[1] is IDropTargetRow dt) dt.DropLineBelow = true;
        Settle(8);
        Shot("wontfit", win);
        if (vm.Overview.DownloadTail.Count > 1 && vm.Overview.DownloadTail[1] is IDropTargetRow dt2) dt2.DropLineBelow = false;
        vm.Overview.ShowWontFitCommand.Execute(null); Settle(6);
        if (vm.Overview.DownloadTail.OfType<QueueRow>().FirstOrDefault(r => r.WontFit) is { } big) big.FileLimit = "FAT32";   // wording check: the over-the-ceiling row
        Settle(4);
        Console.WriteLine($"WONTFIT expanded: tail={vm.Overview.DownloadTail.Count} divider={vm.Overview.DownloadTail.IndexOf(vm.Overview.WontFitDivider)} order=" + string.Join(",", vm.Overview.DownloadTail.Select(o => o is QueueRow q ? (q.WontFit ? "R" : "f") : "|")));
        Shot("wontfit_expanded", win);
        if (Environment.GetEnvironmentVariable("GROG_SHOT_WONTFIT_PULL") == "1")
        {
            vm.Overview.WontFitDivider.SetRoom(new[] { new Grog.Core.Volumes.DeviceSpace("primary", 5_000_000_000L, true) });   // room for the smaller red rows only
            var reds = vm.Overview.DownloadTail.OfType<QueueRow>().Where(r => r.WontFit).ToList();
            Console.WriteLine("WONTFIT canMoveUp=" + string.Join(",", reds.Select(r => $"{r.SizeText}:{vm.Overview.PullUpRowCommand.CanExecute(r)}")));
            vm.Overview.PullUpWhatFitsCommand.Execute(null);
            Console.WriteLine("WONTFIT lit=" + vm.Overview.DownloadTail.OfType<QueueRow>().Count(r => r.JustMoved) + " log=" + string.Join(" | ", vm.Overview.LogEntries.Take(3).Select(l => l.Message))); Settle(1);
            Shot("wontfit_pulled", win); Settle(6);
            Console.WriteLine("WONTFIT pulled: persisted=" + string.Join(",", vm.Session.Manifest!.Current.Downloads.Snapshot().Select(q => q.Fits ? "f" : "R")) + " drawn=" + string.Join(",", vm.Overview.DownloadTail.Select(o => o is QueueRow q ? (q.WontFit ? "R" : "f") : "|")));
        }
        // Drop mapping
        Console.WriteLine("WONTFIT drop map: " + string.Join(" ", Enumerable.Range(0, vm.Overview.DownloadTail.Count).Select(i => $"{i}->{vm.Overview.PersistedIndexOfTailDrop(i, (QueueRow)vm.Overview.DownloadTail.OfType<QueueRow>().Last())}"))
            + " persisted=" + string.Join(",", vm.Session.Manifest!.Current.Downloads.Snapshot().Select(q => q.Fits ? "f" : "R")));
        if (Environment.GetEnvironmentVariable("GROG_SHOT_WONTFIT_ALL") == "1" && vm.Session.Manifest is JsonManifestStore allStore)
        {
            // Every row red
            foreach (var q in allStore.Current.Downloads.Snapshot()) allStore.Current.Downloads.SetPlacement(q.GogId, q.FileKey, q.TargetRootId, fits: false);
            vm.RecomputeActivity(); Settle(6);
            Console.WriteLine($"WONTFIT_ALL: pillState={vm.Overview.DownloadPillState} pillIdle={vm.Overview.DownloadPillIdle} idleText={vm.Overview.DownloadPillIdleText} divider={vm.Overview.DownloadTail.IndexOf(vm.Overview.WontFitDivider)} text={vm.Overview.WontFitDivider.Text} queue={allStore.Current.Downloads.Snapshot().Count}");
            Shot("wontfit_all", win);
        }
        if (Environment.GetEnvironmentVariable("GROG_SHOT_WONTFIT_CONFIRM") == "1")
        {
            vm.Overview.RemoveWontFitCommand.Execute(null); Settle(6);
            Console.WriteLine($"WONTFIT confirm open={vm.Overview.ShowRemoveWontFitConfirm} text={vm.Overview.RemoveWontFitConfirmText}");
            Shot("wontfit_confirm", win);
            vm.Overview.ConfirmRemoveWontFitCommand.Execute(null); Settle(6);
            Console.WriteLine($"WONTFIT after remove: queue={vm.Session.Manifest.Current.Downloads.Snapshot().Count} red={vm.Overview.DownloadTail.OfType<QueueRow>().Count(r => r.WontFit)} divider={vm.Overview.DownloadTail.IndexOf(vm.Overview.WontFitDivider)}");
        }
        return;
    }
    if (Environment.GetEnvironmentVariable("GROG_SHOT_TOAST") == "1") {
        CloseOverlays(); vm.Library.SelectedRow = null;
        vm.Overview.ToastSeverity = 0; vm.Overview.ToastMessage = "Backing up to more than one storage.\nPrimary: 9.05 GB, 12 games\nSecondary: 478.4 GB, 640 games"; vm.Overview.ToastVisible = true; Settle(4);
        Shot("toast_info", win);
        vm.Overview.ToastSeverity = 2; vm.Overview.ToastMessage = "1 file failed in the last backup run. Response status code does not indicate success: 416 (Range Not Satisfiable)."; Settle(4);
        Shot("toast_error", win); return;
    }
    if (Environment.GetEnvironmentVariable("GROG_SETTINGS_MEASURE") == "1") {
        CloseOverlays(); vm.Library.SelectedRow = null; vm.NavigateCommand.Execute("Settings"); Settle(10);
        foreach (var b in win.GetVisualDescendants().OfType<Avalonia.Controls.Border>())
        {
            if (!b.Classes.Contains("scard")) continue;
            var head = b.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>()
                        .FirstOrDefault(t => t.Classes.Contains("sgh"));
            var pt = b.TranslatePoint(new Avalonia.Point(0, 0), win);
            Console.WriteLine($"CARD {head?.Text,-28} x={pt?.X,6:F0} top={pt?.Y,6:F0} h={b.Bounds.Height,6:F0} bottom={(pt?.Y ?? 0) + b.Bounds.Height,6:F0}");
        }
        foreach (var sv in win.GetVisualDescendants().OfType<Avalonia.Controls.ScrollViewer>())
            Console.WriteLine($"SCROLL viewport={sv.Viewport.Height:F0} extent={sv.Extent.Height:F0} over={sv.Extent.Height - sv.Viewport.Height:F0}");
        return;
    }
    if (Environment.GetEnvironmentVariable("GROG_SHOT_INVKEEP") == "1") {
        // Expansion and selection survive the per-settle inventory rebuild.
        CloseOverlays(); vm.NavigateCommand.Execute("Drives"); Settle(6);
        var g0 = vm.Storage.PrimaryGrid.GroupedRows.OfType<InventoryGroupRow>().First();
        g0.IsExpanded = true; g0.Files[0].IsSelected = true; vm.Storage.PrimaryGrid.Selected.Add(g0.Files[0]);
        long id = g0.GogId; string key = g0.Files[0].File.FileKey;
        vm.Storage.RebuildInventory(); Settle(2);
        var g1 = vm.Storage.PrimaryGrid.GroupedRows.OfType<InventoryGroupRow>().First(g => g.GogId == id);
        Console.WriteLine($"INVKEEP same={ReferenceEquals(g0, g1)} expanded={g1.IsExpanded} selected={g1.Files.Any(f => f.File.FileKey == key && f.IsSelected)} gridSel={vm.Storage.PrimaryGrid.Selected.Count}");
        return;
    }
    if (Environment.GetEnvironmentVariable("GROG_SHOT_ADDOFFER") == "1") {
        // The "new storage added" offer, seeded with a finished result.
        CloseOverlays(); vm.NavigateCommand.Execute("Drives"); Settle(6);
        var any = vm.Session.Manifest!.Current.Items.SelectMany(i => i.Files.Select(f => (i, f))).Take(48).ToList();
        var r = new Grog.Core.Volumes.StorageAdditionOffer.Result(262, 527_980_000_000L, 527, 14_300_000_000L, any, any.Take(48).ToList(), 12, 2_100_000_000L, 0);
        vm.Storage.SeedStorageAddOfferForShots(r, @"F:\___GOG_Backup___", @"G:\GOG_Test_16GB___", 4_582_000_000_000L); Settle(6);
        Shot("add_offer", win); return;
    }
    if (Environment.GetEnvironmentVariable("GROG_SHOT_DETACHED") == "1") {
        // A drive with no slot: the OTHER STORAGE section at the bottom, the slot offer in the empty Secondary card, the rail entry.
        CloseOverlays(); vm.NavigateCommand.Execute("Drives"); Settle(6);
        vm.Storage.OtherStorage.Add(new Grog.App.ViewModels.StorageViewModel.OtherStorageRow("shelf1", "Archive 2024", @"E:\GOG Archive", 412, 187_000_000_000L, true,
            new[] { new Grog.App.ViewModels.StorageViewModel.OtherStorageGame("Baldur's Gate 2 Complete", 10, 5_820_000_000L),
                    new Grog.App.ViewModels.StorageViewModel.OtherStorageGame("Sanitarium", 6, 5_400_000_000L) }) { IsExpanded = true });
        vm.Storage.OtherStorage.Add(new Grog.App.ViewModels.StorageViewModel.OtherStorageRow("shelf2", "Old laptop drive", @"H:\GOG", 37, 12_000_000_000L, false,
            new[] { new Grog.App.ViewModels.StorageViewModel.OtherStorageGame("Toonstruck", 5, 2_690_000_000L) }));
        vm.Storage.ShowOtherStorageSection = true;
        typeof(Grog.App.ViewModels.StorageViewModel).GetMethod("OnPropertyChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, new[] { typeof(string) })!
            .Invoke(vm.Storage, new object[] { "HasOtherStorage" });
        typeof(Grog.App.ViewModels.StorageViewModel).GetMethod("OnPropertyChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, new[] { typeof(string) })!
            .Invoke(vm.Storage, new object[] { "OtherStorageRailText" });
        Settle(6);
        Shot("detached_strip", win); return;
    }
    if (Environment.GetEnvironmentVariable("GROG_SHOT_DELETEDEVICE") == "1") {
        // The device-delete progress overlay, seeded mid-run.
        CloseOverlays(); vm.NavigateCommand.Execute("Drives"); Settle(6);
        vm.Storage.DeleteDeviceProgressTitle = "DELETING PRIMARY";
        vm.Storage.DeleteDeviceProgressText = "418 of 1,204 · 61.2 GB freed · 2 failed";
        vm.Storage.DeleteDeviceProgressValue = 34.7; vm.Storage.ShowDeleteDeviceProgress = true; Settle(6);
        Shot("delete_device_progress", win); return;
    }
    if (Environment.GetEnvironmentVariable("GROG_SHOT_DELETE") == "1") {
        // Delete downloaded files: the section-scope entry, which needs no grid selection.
        CloseOverlays(); vm.NavigateCommand.Execute("Library"); Settle(6);
        vm.Library.SelectedRow = vm.Library.Games.FirstOrDefault(r => r.Item is { } it && it.Files.Any(f => f.LocalRelativePath is not null)) ?? vm.Library.Games.FirstOrDefault(); Settle(6);
        vm.Library.DeleteSectionCommand.Execute("games"); Settle(6);
        Console.WriteLine($"DELETE open={vm.Library.ShowDeleteConfirm} text={vm.Library.DeleteConfirmText}");
        Shot("delete_confirm", win); return;
    }
    if (Environment.GetEnvironmentVariable("GROG_SHOT_SCOPEFLYOUT") == "1") {
        // The scope card's CONTENT flyout, open. Its selected-row treatment cannot be judged from the page
        // shot: a flyout is its own popup tree and only appears once something opens it.
        CloseOverlays(); vm.Library.SelectedRow = null; vm.NavigateCommand.Execute("Settings"); Settle(10);
        var picker = win.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
                        .FirstOrDefault(b => b.Name == "ContentPicker");
        if (picker?.Flyout is { } fo) { fo.ShowAt(picker); Settle(10); }
        else Console.WriteLine("ContentPicker not found");
        Shot("scope_flyout", win); return;
    }
    if (Environment.GetEnvironmentVariable("GROG_SHOT_SETTINGS") == "1") {
        CloseOverlays(); vm.Library.SelectedRow = null; vm.NavigateCommand.Execute("Settings"); Settle(8);
        // GROG_SHOT_UPDATE=1
        if (Environment.GetEnvironmentVariable("GROG_SHOT_UPDATE") == "1")
        { vm.Settings.SeedGrogUpdate("0.2.0", "https://example.invalid/release"); Settle(8); }
        Shot("settings", win); return;
    }
    if (Environment.GetEnvironmentVariable("GROG_SHOT_GOG") == "1") {
        CloseOverlays(); vm.Library.SelectedRow = null; vm.NavigateCommand.Execute("Drives");
        ApplyTheme(Grog.App.GrogTheme.OdeToGog); Settle(8);
        Shot("gogtheme", win); return;
    }
    // The two stopped-move dialogs
    if (Environment.GetEnvironmentVariable("GROG_SHOT_VERDICTHOVER") == "1") {
        CloseOverlays(); vm.Library.SelectedRow = null; vm.NavigateCommand.Execute("Overview"); Settle(8);
        var verdict = win.GetVisualDescendants().OfType<Avalonia.Controls.Button>()
                         .FirstOrDefault(b => b.Classes.Contains("verdict"));
        if (verdict is null) { Console.Error.WriteLine("VERDICTHOVER: no Button with class 'verdict' found."); return; }
        var hover = new Avalonia.Styling.Style(x => Avalonia.Styling.Selectors.Class(
            Avalonia.Styling.Selectors.OfType(x, typeof(Avalonia.Controls.TextBlock)), "ledehead"));
        if (Avalonia.Application.Current!.TryGetResource("AccentAmberHover", null, out var amber)
            && amber is Avalonia.Media.IBrush brush)
            hover.Setters.Add(new Avalonia.Styling.Setter(Avalonia.Controls.TextBlock.ForegroundProperty, brush));
        win.Styles.Add(hover);
        Settle(8);
        Shot("verdict_hover", win); return;
    }
    // The first-run guide's spotlight
    if (Environment.GetEnvironmentVariable("GROG_SHOT_SCAN") == "1") {
        CloseOverlays(); vm.NavigateCommand.Execute("Library");
        vm.Library.SeedScanProgress(342, 1206, "Fetching The Witcher 3: Wild Hunt - Game of the Year Edition");
        Settle(10);
        Shot("scanpanel", win); return;
    }
    // GROG_SHOT_WELCOME=1
    if (Environment.GetEnvironmentVariable("GROG_SHOT_WELCOME") == "1") {
        CloseOverlays();
        if (double.TryParse(Environment.GetEnvironmentVariable("GROG_SHOT_BG"), out var bgo)) vm.MockBgOpacity = bgo;
        vm.ShowWelcomeOverlay = true; Settle(6);
        Shot("welcome", win);
        return;
    }

    if (Environment.GetEnvironmentVariable("GROG_SHOT_GUIDE") == "1") {
        CloseOverlays();
        // GROG_SHOT_GUIDE_VIEW=Library|Drives|Accounts|Settings
        if (Environment.GetEnvironmentVariable("GROG_SHOT_GUIDE_VIEW") is { Length: > 0 } gview)
            vm.NavigateCommand.Execute(gview);
        // GROG_SHOT_GUIDE_STEP=<key>
        if (Environment.GetEnvironmentVariable("GROG_SHOT_GUIDE_STEP") is { Length: > 0 } gstep)
            vm.Guide.GuideStepOverride = gstep;
        // GROG_SHOT_GUIDE_SCAN=1: seed a scan in progress
        if (Environment.GetEnvironmentVariable("GROG_SHOT_GUIDE_STEP") == "item-count")
            vm.Library.ShowCompositionModal = true;
        if (Environment.GetEnvironmentVariable("GROG_SHOT_GUIDE_SCAN") == "1")
            vm.Library.SeedScanProgress(17, 158, "Scanning library… 17/158");
        if (Environment.GetEnvironmentVariable("GROG_SHOT_GUIDE_RESUME") is { Length: > 0 } rkey)
        {
            var idx = Grog.Core.Guide.FirstRunTour.IndexOf(rkey);
            var st = vm.Session.Settings;
            st.GuidePosition = idx; st.GuideAcked = Grog.Core.Guide.FirstRunTour.Nodes.Take(idx).Select(n => n.Key).ToList();
            vm.Guide.Resume(); Settle(40);
        }
        else { vm.Guide.StartGuideCommand.Execute(null); Settle(40); }
        Console.WriteLine($"GUIDE target={vm.Guide.GuideTargetName} hasTarget={vm.Guide.GuideHasTarget} "
                        + $"hole=({vm.Guide.GuideHoleX:F1},{vm.Guide.GuideHoleY:F1}) {vm.Guide.GuideHoleW:F1}x{vm.Guide.GuideHoleH:F1}");
        foreach (var n in new[]{"GuideNavOverview","GuideNavLibrary","GuideNavFolders","GuideNavAccounts",
                                "GuideNavSettings","GuideAddAccount","GuideAddFolder","GuideScan","GuideBackUp"})
        {
            var c = win.GetVisualDescendants().OfType<Avalonia.Controls.Control>().FirstOrDefault(x => x.Name == n);
            var pt = c?.TranslatePoint(new Avalonia.Point(0,0), win);
            Console.WriteLine($"  {n,-18} at {(pt is {} q ? $"({q.X:F1},{q.Y:F1})" : "n/a")} size {c?.Bounds.Width:F1}x{c?.Bounds.Height:F1}");
        }
        if (vm.Guide.GuideHasTarget)
        {
            var overlayC = win.GetVisualDescendants().OfType<Avalonia.Controls.Control>().FirstOrDefault(x => x.Name == "GuideOverlayRoot");
            var center = new Avalonia.Point(vm.Guide.GuideHoleX + vm.Guide.GuideHoleW / 2, vm.Guide.GuideHoleY + vm.Guide.GuideHoleH / 2);
            var inWin = overlayC?.TranslatePoint(center, win) ?? center;
            var hit = Avalonia.VisualTree.VisualExtensions.GetVisualsAt(win, inWin).FirstOrDefault();
            Console.WriteLine($"  HITTEST at win({inWin.X:F1},{inWin.Y:F1}):");
            for (var v = hit as Avalonia.Controls.Control; v is not null; v = v.GetVisualParent() as Avalonia.Controls.Control)
                Console.WriteLine($"    {v.GetType().Name} name={v.Name ?? "-"} bounds={v.Bounds} w={v.Width} h={v.Height} margin={v.Margin} hit={v.IsHitTestVisible}");
        }
        // FITS-ON-SCREEN ASSERTION
        {
            var box = win.GetVisualDescendants().OfType<Avalonia.Controls.Control>()
                         .FirstOrDefault(x => x.Name == "GuideCaptionBox");
            var bp = box?.TranslatePoint(new Avalonia.Point(0,0), win);
            if (box is not null && bp is {} b)
            {
                bool fits = b.X >= 0 && b.Y >= 0 && b.X + box.Bounds.Width <= shotW + 0.5
                            && b.Y + box.Bounds.Height <= shotH + 0.5;
                Console.WriteLine($"  CAPTION at ({b.X:F1},{b.Y:F1}) {box.Bounds.Width:F1}x{box.Bounds.Height:F1} "
                                + $"viewport {shotW:F0}x{shotH:F0} -> {(fits ? "FITS" : "OFF-SCREEN")}");
            }
        }
        foreach (var b in win.GetVisualDescendants().OfType<Avalonia.Controls.Button>())
        {
            var txt = b.Content as string ?? "";
            if (!txt.StartsWith("Change Folder")) continue;
            var overlayC2 = win.GetVisualDescendants().OfType<Avalonia.Controls.Control>()
                               .FirstOrDefault(x => x.Name == "GuideOverlayRoot");
            var bp = overlayC2 is null ? null : b.TranslatePoint(new Avalonia.Point(0,0), overlayC2);
            if (bp is not {} q) continue;
            bool inside = q.X >= vm.Guide.GuideHoleX && q.Y >= vm.Guide.GuideHoleY
                          && q.X + b.Bounds.Width  <= vm.Guide.GuideHoleX + vm.Guide.GuideHoleW
                          && q.Y + b.Bounds.Height <= vm.Guide.GuideHoleY + vm.Guide.GuideHoleH;
            Console.WriteLine($"  ACTION \"{txt}\" at ({q.X:F1},{q.Y:F1}) {b.Bounds.Width:F1}x{b.Bounds.Height:F1} "
                            + $"hole=({vm.Guide.GuideHoleX:F1},{vm.Guide.GuideHoleY:F1}) {vm.Guide.GuideHoleW:F1}x{vm.Guide.GuideHoleH:F1} -> "
                            + (inside ? "INSIDE (clickable)" : "OUTSIDE (dimmed)"));
        }
        Shot($"guide_{shotW:F0}x{shotH:F0}", win); return;
    }
    if (Environment.GetEnvironmentVariable("GROG_SHOT_MARKWARN") == "1") {
        CloseOverlays(); vm.NavigateCommand.Execute("Library");
        vm.Library.MarkWarningText = "Grog can see 3 file(s) on disk for this selection. Marking them not backed up "
                           + "won't delete anything, but Grog will download them again.";
        vm.Library.ShowMarkNotBackedUpConfirm = true; Settle(6);
        Shot("markwarn", win); return;
    }
    if (Environment.GetEnvironmentVariable("GROG_SHOT_SCHEDCARDS") == "1") {
        CloseOverlays(); vm.NavigateCommand.Execute("Overview"); Settle(4);
        vm.Schedule.ShowScheduleCountdown = true; vm.Schedule.ScheduleCountdownText = "Starting in 5 seconds"; Settle(6);
        Shot("sched_countdown", win); vm.Schedule.ShowScheduleCountdown = false;
        vm.Schedule.SeedFirstUnattendedNotice("Backed up 12 files (4.20 GB) to F:\\Backup while you were away."); Settle(6);
        Shot("sched_unattended", win);
        vm.Schedule.ScheduleOverdue = true; vm.Schedule.ScheduleOverdueText = "A scheduled backup was due Tuesday, Sep 1 at 3:00 AM, but Grog wasn't running."; Settle(6);
        Shot("sched_overdue", win); return;
    }
    if (Environment.GetEnvironmentVariable("GROG_SHOT_DIALOGS") == "1") {
        CloseOverlays(); vm.NavigateCommand.Execute("Library");
        vm.Library.RemoveItemsConfirmText = "2 games leave the library. Nothing on disk is deleted; a rescan by an owning account adds them back. Use Hide to keep a game off the grid for good.";
        vm.Library.ShowRemoveItemsConfirm = true; Settle(6);
        Shot("dialog_danger", win);
        vm.Library.ShowRemoveItemsConfirm = false; vm.NavigateCommand.Execute("Settings");
        vm.Settings.ShowFreshLibraryConfirm = true; Settle(6);
        Shot("dialog_typed", win);
        vm.Settings.ShowFreshLibraryConfirm = false; vm.NavigateCommand.Execute("Overview"); Settle(4);
        // Three-button dialogs on the shared control
        vm.Storage.ShowChangeFolderPrompt = true; Settle(6); Shot("dialog_alt_danger", win); vm.Storage.ShowChangeFolderPrompt = false;
        vm.Storage.AddIntentFolderName = "GOG Backup"; vm.Storage.PendingSameVolumeWarning = "This folder is on the same drive as your primary storage: a drive failure takes both.";
        vm.Storage.ShowAddIntent = true; Settle(6); Shot("dialog_alt", win); vm.Storage.ShowAddIntent = false;
        vm.Storage.ShowMoveStartedNotice = true; Settle(6); Shot("dialog_move_started", win); vm.Storage.ShowMoveStartedNotice = false;
        vm.ShowNoSpaceSummary = true; Settle(6); Shot("dialog_nospace", win); vm.ShowNoSpaceSummary = false;
        // Fix It with BOTH corrupt and missing files seeded
        vm.Overview.LedeFixNowCommand.Execute(null); Settle(6);
        Shot("dialog_fixall", win); vm.Overview.CancelFixCommand.Execute(null);
        vm.NavigateCommand.Execute("Library"); Settle(4);
        // The shared details file-row template on a game WITH extras
        if (vm.Library.Games.FirstOrDefault(g => g.Item is { } it && it.Files.Any(f => f.Kind == Grog.Core.Models.FileKind.Extra)) is { } withExtras)
        {
            vm.Library.SelectedRow = withExtras; Settle(6);
            Shot("dialog_detail_extras", win);
        }
        // The shared FilterPanel
        if (win.GetLogicalDescendants().OfType<Avalonia.Controls.Primitives.Popup>().FirstOrDefault(x => x.Name == "ExtrasFilterPopup") is { } pop)
        {
            pop.PlacementTarget = win; pop.IsOpen = true; Settle(6);
            Shot("dialog_filter", win); pop.IsOpen = false;
        }
        return;
    }
    if (Environment.GetEnvironmentVariable("GROG_SHOT_STOPMOVE") == "1") {
        CloseOverlays(); vm.Library.SelectedRow = null; vm.NavigateCommand.Execute("Overview");
        vm.Storage.MoveStopWhat = "Moving files to Archive";
        vm.Storage.MoveStopSummary = "Moving files to Archive was stopped. 7 file(s) moved, and Grog is tracking them there.";
        vm.Storage.ShowMoveStopChoice = true; Settle(6);
        Shot("stopmove_choice", win);
        vm.Storage.ShowMoveStopChoice = false;
        vm.Storage.MoveStopOrphanWarning = "7 file(s) stay where they moved to and stay tracked. Your library is just "
                                 + "half in each arrangement. Nothing is deleted.";
        vm.Storage.ShowOrphanConfirm = true; Settle(6);
        Shot("stopmove_orphan", win);
        vm.Storage.ShowOrphanConfirm = false;
        vm.Storage.MoveStopWhat = "Changing the location of the Primary folder";
        vm.Storage.RevertOldFolder = @"D:\GOG_Backup";
        vm.Storage.RevertNewFolder = @"F:\GOG_Backup";
        vm.Storage.Reverting = true; Settle(6);
        Shot("stopmove_reverting", win);
        vm.Storage.ShowRevertStopConfirm = true; Settle(6);
        Shot("stopmove_revertstop", win);
        vm.Storage.Reverting = false; vm.Storage.ShowRevertStopConfirm = false;
        return;
    }
    if (Environment.GetEnvironmentVariable("GROG_SHOT_ABOUT") == "1") {
        CloseOverlays(); vm.Library.SelectedRow = null; vm.NavigateCommand.Execute("About"); Settle(6);
        Shot("about", win); return;
    }
    // GROG_SHOT_RAILWIDE=1
    if (Environment.GetEnvironmentVariable("GROG_SHOT_RAILWIDE") == "1") {
        CloseOverlays();
        vm.RailCollapsed = false;
        vm.MeasureRailWorstCase();
        Settle(10);
        Shot("railwide", win);
        return;
    }

    // GROG_SHOT_SIGNEDOUT=<view>
    if (Environment.GetEnvironmentVariable("GROG_SHOT_SIGNEDOUT") is { Length: > 0 } sov) {
        CloseOverlays();
        vm.IsConnected = false;
        vm.NavigateCommand.Execute(sov); Settle(10);
        if (Environment.GetEnvironmentVariable("GROG_SHOT_GATE") == "1") { vm.ShowAccountGate = true; vm.AccountGateWhat = "scan your GOG library"; Settle(6); }
        Shot("signedout_" + sov.ToLowerInvariant(), win);
        return;
    }

    if (Environment.GetEnvironmentVariable("GROG_SHOT_LIB") == "1") {
        CloseOverlays(); AllChipsOn(); vm.Library.SelectedRow = null; vm.NavigateCommand.Execute("Library"); Settle(6);
        if (Environment.GetEnvironmentVariable("GROG_SHOT_ALLCHIPS") == "1") {
            foreach (var c in vm.Library.ExtraFilterChips) c.IsVisible = true;
            Settle(20);
        }
        var scroll = win.GetVisualDescendants().OfType<Avalonia.Controls.ScrollViewer>().FirstOrDefault(x => x.Name == "PillScroll");
        var bar = win.GetVisualDescendants().OfType<Avalonia.Controls.Control>().FirstOrDefault(x => x.Name == "PillBar");
        if (Environment.GetEnvironmentVariable("GROG_PILL_PARTS") == "1") {
            vm.Library.PillMode = LibraryViewModel.PillDisplayMode.Full; Settle(10);
            var bar2 = win.GetVisualDescendants().OfType<Avalonia.Controls.Control>().FirstOrDefault(x => x.Name == "PillBar");
            if (bar2 is null) { Console.Error.WriteLine("PILL_PARTS: no control named 'PillBar' in the visual tree."); return; }
            double tot = 0;
            foreach (var ch in bar2.GetVisualChildren().OfType<Avalonia.Controls.Control>()) {
                Console.WriteLine($"PART {ch.GetType().Name} w={ch.Bounds.Width:F1}");
                tot += ch.Bounds.Width;
            }
            foreach (var b3 in bar2.GetVisualDescendants().OfType<Avalonia.Controls.Button>())
                Console.WriteLine($"  CHIP w={b3.Bounds.Width:F1} tip={b3.GetValue(Avalonia.Controls.ToolTip.TipProperty)}");
            Console.WriteLine($"PARTS total={tot:F1} barWidth={bar2.Bounds.Width:F1}");
            return;
        }
        if (Environment.GetEnvironmentVariable("GROG_PILL_SWEEP") == "1") {
            var sc = win.GetVisualDescendants().OfType<Avalonia.Controls.ScrollViewer>().FirstOrDefault(x => x.Name == "PillScroll");
            foreach (var w in new double[]{760,700,640,580,520,470,400,470,520,580,640,700,760}) {
                vm.Library.DetailColumnWidth = new Avalonia.Controls.GridLength(w);
                Settle(8);
                Console.WriteLine($"SWEEP detail={w} mode={vm.Library.PillMode} viewport={sc?.Viewport.Width:F0}");
            }
            return;
        }
        var row = win.GetVisualDescendants().OfType<Avalonia.Controls.Control>().FirstOrDefault(x => x.Name == "ActionRow");
        var head = win.GetVisualDescendants().OfType<Avalonia.Controls.Control>().FirstOrDefault(x => x.Name == "LibHead");
        Console.WriteLine($"CHIPS mode={vm.Library.PillMode} viewport={scroll?.Viewport.Width:F1} natural={bar?.Bounds.Width:F1} visible={vm.Library.ExtraFilterChips.Count(c => c.IsVisible) + vm.Library.SoftwareChips.Count}");
        Console.WriteLine($"WIDTHS actionRow={row?.Bounds.Width:F1} libHead={head?.Bounds.Width:F1} window={win.Bounds.Width:F1}");
        if (Environment.GetEnvironmentVariable("GROG_SHOT_SCOPEMIX") == "1")
            foreach (var o in vm.Library.StatusOptions) o.IsChecked = true;   // opt Not in Scope in for the shot
        Settle(6);
        Shot("library", win); return;
    }
    if (Environment.GetEnvironmentVariable("GROG_SHOT_FOLDERS") == "1") {
        CloseOverlays(); vm.Library.SelectedRow = null; vm.NavigateCommand.Execute("Drives"); Settle(6);
        if (vm.Storage.ShowMigratePrompt) vm.Storage.SkipMigrateCommand.Execute(null); Settle(3);
        // GROG_SHOT_FOLDERS_EXPAND=1
        if (Environment.GetEnvironmentVariable("GROG_SHOT_FOLDERS_EXPAND") == "1")
        {
            vm.Storage.PrimaryGrid.ExpandAllCommand.Execute(null); vm.Storage.SecondaryGrid.ExpandAllCommand.Execute(null); Settle(3);
            if (vm.Storage.PrimaryGrid.FileRows.Count > 0) { vm.Storage.ClickFile(vm.Storage.PrimaryGrid.FileRows[0], ctrl: false); Settle(3); }
        }
        // GROG_SHOT_STORAGE_DIALOGS=1
        if (Environment.GetEnvironmentVariable("GROG_SHOT_STORAGE_DIALOGS") == "1")
        {
            var st = vm.Storage;
            var sec = st.BackupLocations.FirstOrDefault(l => !l.IsPrimary);
            void ShotThen(string name, Action open, Action close) { open(); Settle(3); Shot("sdlg_" + name, win); close(); Settle(2); }
            ShotThen("delete_device", () => st.RequestDeleteDeviceCommand.Execute(sec), () => st.ShowDeleteDeviceDialog = false);
            ShotThen("make_primary", () => st.RequestMakePrimaryCommand.Execute(sec), () => st.ShowMakePrimaryWarning = false);
            ShotThen("lost_device", () => st.RequestLostDeviceCommand.Execute(sec), () => st.ShowLostDeviceDialog = false);
            ShotThen("add_intent", () => { st.AddIntentFolderName = "D:\\GOG Backup"; st.ShowAddIntent = true; }, () => st.ShowAddIntent = false);
            ShotThen("migrate", () => { st.MigrateSummary = "12 file(s) across 4 game(s) are in the old flat layout."; st.ShowMigratePrompt = true; }, () => st.ShowMigratePrompt = false);
            ShotThen("location_error", () => { st.LocationErrorText = "That folder is inside an existing backup folder."; st.ShowLocationErrorModal = true; }, () => st.ShowLocationErrorModal = false);
            ShotThen("move_started", () => st.ShowMoveStartedNotice = true, () => st.ShowMoveStartedNotice = false);
            return;
        }
        Shot("folders", win); return;
    }
    // NO NavigateCommand here on purpose
    CloseOverlays(); vm.Library.SelectedRow = null; Settle(6);
    if (Environment.GetEnvironmentVariable("GROG_SHOT_EXPAND") == "1") { vm.Overview.GoneExpanded = true; vm.Overview.CorruptExpanded = true; Settle(4); }
    if (Environment.GetEnvironmentVariable("GROG_SHOT_MODAL") == "1") { vm.Overview.OpenIssuesCommand.Execute(null); Settle(4); }
    // GROG_SHOT_FIXCONFIRM=1
    if (Environment.GetEnvironmentVariable("GROG_SHOT_FIXCONFIRM") == "1") { vm.Overview.LedeFixNowCommand.Execute(null); Settle(4); }
    Shot("status_issues" + issues, win);
});

