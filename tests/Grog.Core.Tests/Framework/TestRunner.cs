// TestRunner.cs -- ported from MRServer.Tests.Framework (same discovery, isolation, and
// dashboard semantics): discovers every [Test] method by reflection, runs each in isolation
// (fresh instance per instance-method test; [Setup]/[Teardown] bracketing), classifies the
// outcome (PASS / FAIL = failed assertion / ERROR = unexpected exception / SKIP), and prints
// a dot-aligned per-class dashboard with full detail only on failure. Returns 0 iff every
// discovered test passed or was skipped. No manual registration: writing a [Test] is enough.
//
// GROG EXTENSIONS (marked below, backportable to MRServer):
//   1. async support -- [Test] methods may return Task; the runner awaits them.
//   2. runtime skip -- Assert.Skip/SkipUnless throws SkipException, classified as SKIP.
//   3. per-test timeout -- 120 s by default, [Test(TimeoutSeconds = n)] or GROG_TEST_TIMEOUT overrides; a test
//      that overruns is a FAIL and the run continues (the test's Task is left to finish on its own, never aborted).
//   4. per-class isolation -- GROG_CONFIG_DIR is restored and Core's process-static caches are cleared before
//      every class, so class order cannot matter.
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests.Framework;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

public static class TestRunner
{
    enum Outcome { Pass, Fail, Error, Skip }

    sealed record Result(string Class, string Test, Outcome Outcome, string? Detail);

    public static int Run(string? filterCategory = null)
    {
        // Tests create real directories under the system temp folder (49 call sites; only a handful have a
        // [Teardown]). A run leaves roughly 30MB behind - the log-rotation tests allocate a 10MB file, the
        // verify tests a 66MB one - so a machine that runs the suite often accumulates gigabytes, and in a
        // container it fills the disk and surfaces as IOException inside unrelated tests. That reads as a
        // code regression, which is the expensive part.
        //
        // Swept HERE rather than at 49 call sites so new tests are covered without remembering anything.
        // A strict BEFORE/AFTER diff, never a pattern match: only directories that did not exist when the
        // run started are removed. "Grog never deletes files it did not create" is a project rule, and it
        // applies to its own test suite.
        var tempBefore = SnapshotTemp();
        try { return RunCore(filterCategory); }
        finally { SweepTemp(tempBefore); }
    }

    static HashSet<string> SnapshotTemp()
    {
        try { return Directory.GetDirectories(Path.GetTempPath(), "grog-*").ToHashSet(StringComparer.Ordinal); }
        catch { return new HashSet<string>(StringComparer.Ordinal); }
    }

    static void SweepTemp(HashSet<string> before)
    {
        try
        {
            foreach (var d in Directory.GetDirectories(Path.GetTempPath(), "grog-*"))
            {
                if (before.Contains(d)) continue;         // predates this run: not ours to touch
                try { Directory.Delete(d, recursive: true); } catch { /* in use, or gone already */ }
            }
        }
        catch { /* cleanup is best-effort and must never change a run's verdict */ }
    }

    const string ConfigDirVar = "GROG_CONFIG_DIR";
    const int DefaultTimeoutSeconds = 120;

    /// <summary>The per-test limit: the attribute, else GROG_TEST_TIMEOUT, else 120 s.</summary>
    internal static int TimeoutFor(TestAttribute attr)
    {
        if (attr.TimeoutSeconds > 0) return attr.TimeoutSeconds;
        var env = Environment.GetEnvironmentVariable("GROG_TEST_TIMEOUT");
        return int.TryParse(env, out var n) && n > 0 ? n : DefaultTimeoutSeconds;
    }

    static int RunCore(string? filterCategory)
    {
        var asm = Assembly.GetExecutingAssembly();
        var configDirAtStart = Environment.GetEnvironmentVariable(ConfigDirVar);
        static bool IsNewBatch(Type t) => t.GetCustomAttribute<NewBatchAttribute>() != null;
        var discovered = asm.GetTypes()
            .Where(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                         .Any(m => m.GetCustomAttribute<TestAttribute>() != null))
            .ToList();
        var classes = discovered.Where(t => !IsNewBatch(t)).OrderBy(t => t.Name)
                      .Concat(discovered.Where(IsNewBatch).OrderBy(t => t.Name))
                      .ToList();

        var results = new List<Result>();
        bool newDividerShown = false;
        Console.WriteLine();
        Console.WriteLine("==================== GROG SUITE ([Test] discovery) ====================");

        foreach (var type in classes)
        {
            var classTraits = type.GetCustomAttributes<TraitAttribute>().Select(a => a.Category).ToHashSet();
            var classSkip = type.GetCustomAttribute<SkipAttribute>();

            var setup = FindHook<SetupAttribute>(type);
            var teardown = FindHook<TeardownAttribute>(type);

            // Each class starts from the run's own environment: ~20 classes point GROG_CONFIG_DIR at a temp
            // profile and two restore it, and Core keeps process-wide caches (keyring, waiting drives).
            Environment.SetEnvironmentVariable(ConfigDirVar, configDirAtStart);
            Grog.Core.TestHooks.ResetProcessState();

            var tests = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                            .Where(m => m.GetCustomAttribute<TestAttribute>() != null)
                            .OrderBy(m => m.MetadataToken)
                            .ToList();

            var classResults = new List<Result>();
            foreach (var m in tests)
            {
                var traits = classTraits.Concat(m.GetCustomAttributes<TraitAttribute>().Select(a => a.Category)).ToHashSet();
                if (filterCategory != null && !traits.Contains(filterCategory)) continue;

                var attr = m.GetCustomAttribute<TestAttribute>()!;
                string testName = attr.Name ?? m.Name;
                var skip = m.GetCustomAttribute<SkipAttribute>() ?? classSkip;
                if (skip != null) { classResults.Add(new(type.Name, testName, Outcome.Skip, skip.Reason)); continue; }

                classResults.Add(RunOneWithTimeout(type, m, setup, teardown, testName, TimeoutFor(attr)));
            }

            results.AddRange(classResults);
            if (classResults.Count > 0)
            {
                if (IsNewBatch(type) && !newDividerShown)
                {
                    Console.WriteLine(new string('-', 35) + " NEW " + new string('-', 35));
                    newDividerShown = true;
                }
                PrintClassLine(type.Name, classResults);
            }
            foreach (var r in classResults.Where(r => r.Outcome is Outcome.Fail or Outcome.Error))
                Console.WriteLine($"      [{r.Outcome.ToString().ToUpper()}] {r.Test}\n          {Indent(r.Detail)}");
        }

        Environment.SetEnvironmentVariable(ConfigDirVar, configDirAtStart);
        WaitForTimedOut();
        return Summarize(results);
    }

    /// <summary>Tasks of tests that timed out and are still running; drained once, bounded, before the summary.</summary>
    static readonly List<(string Name, Task Work)> _overrun = new();

    static void WaitForTimedOut()
    {
        var pending = _overrun.Where(o => !o.Work.IsCompleted).ToList();
        if (pending.Count == 0) return;
        try { Task.WaitAll(pending.Select(o => o.Work).ToArray(), TimeSpan.FromSeconds(10)); } catch { }
        var still = pending.Where(o => !o.Work.IsCompleted).Select(o => o.Name).ToList();
        if (still.Count > 0)
            Console.WriteLine($"{still.Count} test(s) still running at exit: {string.Join(", ", still)}");
    }

    /// <summary>Runs the test on its own Task and races it against the limit. A test that overruns is a FAIL
    /// ("timed out after N s"); its Task is left running (no thread abort) and the runner moves on with the
    /// real console restored, so the dashboard keeps printing.</summary>
    static Result RunOneWithTimeout(Type type, MethodInfo m, MethodInfo? setup, MethodInfo? teardown, string testName, int timeoutSeconds)
    {
        var realOut = Console.Out;
        var realErr = Console.Error;
        var work = Task.Run(() => RunOne(type, m, setup, teardown, testName));
        var first = Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds))).GetAwaiter().GetResult();
        if (first == work) return work.Result;
        // Restore the real console at once: the overrunning test's own late restore then targets a writer nobody reads.
        Console.SetOut(realOut); Console.SetError(realErr);
        _overrun.Add(($"{type.Name}.{testName}", work));
        return new(type.Name, testName, Outcome.Fail, $"timed out after {timeoutSeconds} s");
    }

    static Result RunOne(Type type, MethodInfo m, MethodInfo? setup, MethodInfo? teardown, string testName)
    {
        var realOut = Console.Out;
        var realErr = Console.Error;
        var buffer = new System.IO.StringWriter();
        object? instance = null;
        Console.SetOut(buffer); Console.SetError(buffer);
        try
        {
            if (!m.IsStatic) instance = Activator.CreateInstance(type);   // fresh instance per test
            InvokeMaybeAsync(setup, setup?.IsStatic == true ? null : instance);
            try
            {
                InvokeMaybeAsync(m, m.IsStatic ? null : instance);
                return new(type.Name, testName, Outcome.Pass, null);
            }
            finally
            {
                InvokeMaybeAsync(teardown, teardown?.IsStatic == true ? null : instance);
            }
        }
        catch (TargetInvocationException tie) when (Unwrap(tie) is SkipException se)      // GROG EXTENSION
        {
            return new(type.Name, testName, Outcome.Skip, se.Message);
        }
        catch (TargetInvocationException tie) when (Unwrap(tie) is AssertException ae)
        {
            return new(type.Name, testName, Outcome.Fail, ae.Message + Captured(buffer));
        }
        catch (TargetInvocationException tie)
        {
            var ex = Unwrap(tie);
            return new(type.Name, testName, Outcome.Error, $"{ex.GetType().Name}: {ex.Message}" + Captured(buffer));
        }
        catch (SkipException se)                                                          // GROG EXTENSION
        {
            return new(type.Name, testName, Outcome.Skip, se.Message);
        }
        catch (AssertException ae)
        {
            return new(type.Name, testName, Outcome.Fail, ae.Message + Captured(buffer));
        }
        catch (Exception ex)
        {
            return new(type.Name, testName, Outcome.Error, $"{ex.GetType().Name}: {ex.Message}" + Captured(buffer));
        }
        finally
        {
            Console.SetOut(realOut); Console.SetError(realErr);
        }
    }

    // GROG EXTENSION: awaits Task-returning [Test]/[Setup]/[Teardown] methods so async tests
    // actually run to completion. Exceptions inside the Task surface with their original type
    // (GetAwaiter().GetResult() does not wrap in AggregateException).
    static void InvokeMaybeAsync(MethodInfo? method, object? instance)
    {
        if (method is null) return;
        var result = method.Invoke(instance, null);
        if (result is Task task) task.GetAwaiter().GetResult();
    }

    static Exception Unwrap(TargetInvocationException tie) => tie.InnerException ?? tie;

    static string Captured(System.IO.StringWriter buffer)
    {
        var text = buffer.ToString().TrimEnd();
        return text.Length == 0 ? "" : "\n  --- captured output ---\n" + text;
    }

    static MethodInfo? FindHook<T>(Type t) where T : Attribute =>
        t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
         .FirstOrDefault(m => m.GetCustomAttribute<T>() != null);

    static void PrintClassLine(string name, List<Result> rs)
    {
        int pass = rs.Count(r => r.Outcome == Outcome.Pass);
        int skip = rs.Count(r => r.Outcome == Outcome.Skip);
        bool bad = rs.Any(r => r.Outcome is Outcome.Fail or Outcome.Error);
        string verdict = bad ? "FAIL" : (skip == rs.Count ? "SKIP" : "PASS");
        string label = $"{name} ({pass}/{rs.Count - skip})" + (skip > 0 ? $" +{skip} skip" : "");
        Console.WriteLine($"{label} {new string('.', Math.Max(3, 52 - label.Length))} {verdict}");
    }

    static int Summarize(List<Result> results)
    {
        int pass = results.Count(r => r.Outcome == Outcome.Pass);
        int fail = results.Count(r => r.Outcome == Outcome.Fail);
        int error = results.Count(r => r.Outcome == Outcome.Error);
        int skip = results.Count(r => r.Outcome == Outcome.Skip);
        Console.WriteLine("---------------------------------------------------------------------------");
        // Skips by reason, so a tally that differs between two machines (no network, no GOG session, no
        // ground-truth folder) explains itself without opening the class lines (owner ask 09-06).
        if (skip > 0)
        {
            Console.WriteLine("Skipped:");
            foreach (var g in results.Where(r => r.Outcome == Outcome.Skip)
                                     .GroupBy(r => string.IsNullOrWhiteSpace(r.Detail) ? "(no reason given)" : r.Detail.Trim())
                                     .OrderByDescending(g => g.Count()).ThenBy(g => g.Key))
            {
                var classes = string.Join(", ", g.Select(r => r.Class).Distinct());
                Console.WriteLine($"  {g.Count(),3}  {g.Key}  [{classes}]");
            }
        }
        Console.WriteLine($"GROG: {pass} passed, {fail} failed, {error} errored, {skip} skipped " +
                          $"({results.Count} discovered)");
        return (fail + error) == 0 ? 0 : 1;
    }

    static string Indent(string? s) => (s ?? "").Replace("\n", "\n          ");
}
