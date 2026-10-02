// Grog container build - the MSBuild stand-in.
//
// Avalonia's .axaml pipeline is two MSBuild tasks, and there is no MSBuild in this sandbox (nuget
// ships none). Both tasks are ordinary classes with settable properties and an Execute(), so this
// driver constructs them by reflection, hands them a minimal IBuildEngine, and calls Execute.
//
// Two modes:
//   resources <projectDir> <outBlob> <listFile>
//       listFile: one "absolutePath|linkPath" per line. The LINK half is not optional - without it
//       the generated avares:// URIs come out ABSOLUTE, and then every StyleInclude in the app
//       fails to resolve at run time with no useful error.
//   xaml <projectDir> <inputAsm> <outputAsm> <refListFile>
//       The task reads its output path from the AssemblyFile item's AvaloniaCompileOutput METADATA,
//       not from the ItemSpec, and NullReferences without it.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Build.Framework;

internal sealed class Item : ITaskItem
{
    private readonly Dictionary<string, string> _meta = new(StringComparer.OrdinalIgnoreCase);
    public Item(string spec) { ItemSpec = spec; }
    public string ItemSpec { get; set; }
    public ICollection MetadataNames => _meta.Keys;
    public int MetadataCount => _meta.Count;
    public string GetMetadata(string name) => name switch
    {
        "FullPath"      => Path.GetFullPath(ItemSpec),
        "Filename"      => Path.GetFileNameWithoutExtension(ItemSpec),
        "Extension"     => Path.GetExtension(ItemSpec),
        "Identity"      => ItemSpec,
        _               => _meta.TryGetValue(name, out var v) ? v : "",
    };
    public void SetMetadata(string name, string value) => _meta[name] = value;
    public void RemoveMetadata(string name) => _meta.Remove(name);
    public void CopyMetadataTo(ITaskItem d) { foreach (var kv in _meta) d.SetMetadata(kv.Key, kv.Value); }
    public IDictionary CloneCustomMetadata() => new Dictionary<string, string>(_meta);
}

internal sealed class Engine : IBuildEngine
{
    public bool Failed;
    public bool ContinueOnError => false;
    public int LineNumberOfTaskNode => 0;
    public int ColumnNumberOfTaskNode => 0;
    public string ProjectFileOfTaskNode => "grog.container.build";
    public void LogErrorEvent(BuildErrorEventArgs e)
    {
        Failed = true;
        Console.Error.WriteLine($"ERROR {e.File}({e.LineNumber},{e.ColumnNumber}): {e.Code} {e.Message}");
    }
    public void LogWarningEvent(BuildWarningEventArgs e) =>
        Console.Error.WriteLine($"warning {e.File}({e.LineNumber}): {e.Code} {e.Message}");
    public void LogMessageEvent(BuildMessageEventArgs e)
    {
        if (e.Importance == MessageImportance.High) Console.WriteLine(e.Message);
    }
    public void LogCustomEvent(CustomBuildEventArgs e) { }
    public bool BuildProjectFile(string p, string[] t, IDictionary g, IDictionary o) => false;
}

internal static class Driver
{
    private static Assembly _tasks = null!;

    private static int Main(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: avdriver <tasksDll> resources|xaml ..."); return 2; }
        var tasksDll = args[0];
        // The task assembly pulls in MSBuild + its own bundled XamlX/Cecil from the same folder.
        var probeDir = Path.GetDirectoryName(Path.GetFullPath(tasksDll))!;
        AssemblyLoadContext_Hook(probeDir);
        _tasks = Assembly.LoadFrom(tasksDll);

        return args[1] switch
        {
            "resources" => Resources(args.Skip(2).ToArray()),
            "xaml"      => Xaml(args.Skip(2).ToArray()),
            _           => Fail($"unknown mode '{args[1]}'"),
        };
    }

    private static void AssemblyLoadContext_Hook(string dir) =>
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var name = new AssemblyName(e.Name).Name + ".dll";
            var p = Path.Combine(dir, name);
            return File.Exists(p) ? Assembly.LoadFrom(p) : null;
        };

    private static int Fail(string msg) { Console.Error.WriteLine("avdriver: " + msg); return 2; }

    private static object New(string typeName)
    {
        var t = _tasks.GetType(typeName) ?? throw new InvalidOperationException($"no type {typeName}");
        return Activator.CreateInstance(t)!;
    }

    private static void Set(object task, string prop, object? value)
    {
        var p = task.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"no property {prop} on {task.GetType().Name}");
        p.SetValue(task, value);
    }

    private static int Run(object task, Engine engine)
    {
        var m = task.GetType().GetMethod("Execute", Type.EmptyTypes)!;
        var ok = (bool)m.Invoke(task, null)!;
        return ok && !engine.Failed ? 0 : 1;
    }

    private static int Resources(string[] a)
    {
        if (a.Length != 3) return Fail("resources <projectDir> <outBlob> <listFile>");
        string projectDir = Path.GetFullPath(a[0]), outBlob = Path.GetFullPath(a[1]);

        var items = new List<ITaskItem>();
        foreach (var line in File.ReadAllLines(a[2]))
        {
            if (line.Length == 0) continue;
            var bar = line.IndexOf('|');
            var full = bar < 0 ? line : line[..bar];
            var link = bar < 0 ? Path.GetRelativePath(projectDir, full) : line[(bar + 1)..];
            var it = new Item(full);
            it.SetMetadata("Link", link.Replace('\\', '/'));
            items.Add(it);
        }

        var engine = new Engine();
        var task = New("Avalonia.Build.Tasks.GenerateAvaloniaResourcesTask");
        Set(task, "BuildEngine", engine);
        Set(task, "Resources", items.ToArray());
        Set(task, "Root", projectDir);
        Set(task, "Output", outBlob);
        Directory.CreateDirectory(Path.GetDirectoryName(outBlob)!);
        var rc = Run(task, engine);
        if (rc == 0) Console.WriteLine($"blob: {outBlob} ({new FileInfo(outBlob).Length} bytes, {items.Count} items)");
        return rc;
    }

    private static int Xaml(string[] a)
    {
        if (a.Length != 4) return Fail("xaml <projectDir> <inputAsm> <outputAsm> <refListFile>");
        string projectDir = Path.GetFullPath(a[0]), input = Path.GetFullPath(a[1]), output = Path.GetFullPath(a[2]);

        var asmItem = new Item(input);
        // THE metadata the task actually reads for its destination. Setting only the ItemSpec gives
        // a NullReferenceException deep inside the task with nothing pointing at the cause.
        asmItem.SetMetadata("AvaloniaCompileOutput", output);

        var refs = File.ReadAllLines(a[3])
                       .Where(l => l.Length > 0 && File.Exists(l))
                       .Select(l => (ITaskItem)new Item(l))
                       .ToArray();

        var engine = new Engine();
        var task = New("Avalonia.Build.Tasks.CompileAvaloniaXamlTask");
        Set(task, "BuildEngine", engine);
        Set(task, "ProjectDirectory", projectDir);
        Set(task, "AssemblyFile", asmItem);
        Set(task, "References", refs);
        Set(task, "DefaultCompileBindings", true);   // matches AvaloniaUseCompiledBindingsByDefault in the csproj
        Set(task, "VerifyIl", false);
        Set(task, "SkipXamlCompilation", false);
        Set(task, "ReportImportance", "high");
        Set(task, "AnalyzerConfigFiles", Array.Empty<ITaskItem>());   // null here throws inside the task

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var rc = Run(task, engine);
        if (rc == 0 && File.Exists(output))
            Console.WriteLine($"xaml: {output} ({new FileInfo(output).Length} bytes, {refs.Length} refs)");
        return rc;
    }
}
