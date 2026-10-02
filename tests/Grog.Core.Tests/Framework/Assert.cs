// Grog test framework -- assertions. Failed assertions throw AssertException, which the
// runner classifies as FAIL (vs ERROR for unexpected exceptions), matching MRServer.
// GROG EXTENSION: Assert.Skip / SkipException for runtime skips (e.g. "no stored GOG
// token") -- the runner classifies these as SKIP.
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests.Framework;

using System;
using System.Collections.Generic;
using System.Linq;

public sealed class AssertException : Exception
{
    public AssertException(string message) : base(message) { }
}

public sealed class SkipException : Exception
{
    public SkipException(string reason) : base(reason) { }
}

public static class Assert
{
    public static void True(bool condition, string? label = null)
    {
        if (!condition) throw new AssertException(label ?? "Expected true, got false.");
    }

    public static void False(bool condition, string? label = null)
    {
        if (condition) throw new AssertException(label ?? "Expected false, got true.");
    }

    public static void Equal<T>(T expected, T actual, string? label = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new AssertException($"{Prefix(label)}Expected: {Render(expected)}  Actual: {Render(actual)}");
    }

    public static void NotEqual<T>(T notExpected, T actual, string? label = null)
    {
        if (EqualityComparer<T>.Default.Equals(notExpected, actual))
            throw new AssertException($"{Prefix(label)}Did not expect: {Render(actual)}");
    }

    public static void Null(object? value, string? label = null)
    {
        if (value is not null)
            throw new AssertException($"{Prefix(label)}Expected null, got: {Render(value)}");
    }

    public static void NotNull(object? value, string? label = null)
    {
        if (value is null) throw new AssertException($"{Prefix(label)}Expected non-null.");
    }

    public static void Empty<T>(IEnumerable<T> items, string? label = null)
    {
        var count = items.Count();
        if (count != 0) throw new AssertException($"{Prefix(label)}Expected empty, found {count} item(s).");
    }

    public static void NotEmpty<T>(IEnumerable<T> items, string? label = null)
    {
        if (!items.Any()) throw new AssertException($"{Prefix(label)}Expected at least one item.");
    }

    /// <summary>Asserts exactly one item and returns it (mirrors xunit's most useful helper).</summary>
    public static T Single<T>(IEnumerable<T> items, string? label = null)
    {
        var list = items.Take(2).ToList();
        if (list.Count != 1)
            throw new AssertException($"{Prefix(label)}Expected exactly one item, found {(list.Count == 0 ? "none" : "several")}.");
        return list[0];
    }

    public static void Contains(string expectedSubstring, string actual, string? label = null)
    {
        if (actual is null || !actual.Contains(expectedSubstring, StringComparison.Ordinal))
            throw new AssertException($"{Prefix(label)}Expected substring \"{expectedSubstring}\" in: {Render(actual)}");
    }

    public static void All<T>(IEnumerable<T> items, Action<T> check, string? label = null)
    {
        int i = 0;
        foreach (var item in items)
        {
            try { check(item); }
            catch (AssertException ex)
            {
                throw new AssertException($"{Prefix(label)}Item[{i}] failed: {ex.Message}");
            }
            i++;
        }
    }

    public static TException Throws<TException>(Action action, string? label = null) where TException : Exception
    {
        try { action(); }
        catch (TException ex) { return ex; }
        catch (Exception ex)
        {
            throw new AssertException($"{Prefix(label)}Expected {typeof(TException).Name}, got {ex.GetType().Name}: {ex.Message}");
        }
        throw new AssertException($"{Prefix(label)}Expected {typeof(TException).Name}, but nothing was thrown.");
    }

    // --- GROG EXTENSION: runtime skip ---
    public static void Skip(string reason) => throw new SkipException(reason);

    public static void SkipUnless(bool condition, string reason)
    {
        if (!condition) throw new SkipException(reason);
    }

    private static string Prefix(string? label) => label is null ? "" : label + " - ";
    private static string Render(object? v) => v switch
    {
        null => "null",
        string s => $"\"{(s.Length <= 120 ? s : s[..120] + "…")}\"",
        _ => v.ToString() ?? "?",
    };
}
