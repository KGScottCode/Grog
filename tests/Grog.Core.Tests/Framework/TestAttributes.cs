// Grog test framework -- attribute set matching the MRServer.Tests.Framework contract,
// so muscle memory transfers 1:1 between projects.
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests.Framework;

using System;

/// <summary>Marks a method as a test. Optional display name overrides the method name; TimeoutSeconds overrides the
/// runner's per-test limit (0 = the default).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute
{
    public string? Name { get; }
    public int TimeoutSeconds { get; set; }
    public TestAttribute(string? name = null) => Name = name;
}

/// <summary>Runs before each [Test] in the class (fresh instance per test).</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SetupAttribute : Attribute { }

/// <summary>Runs after each [Test] in the class, even when the test throws.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class TeardownAttribute : Attribute { }

/// <summary>Category tag; TestRunner.Run("category") runs only matching tests. Class- or method-level.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public sealed class TraitAttribute : Attribute
{
    public string Category { get; }
    public TraitAttribute(string category) => Category = category;
}

/// <summary>Statically skips a test (or a whole class) with a reason.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class SkipAttribute : Attribute
{
    public string Reason { get; }
    public SkipAttribute(string reason) => Reason = reason;
}

/// <summary>Sinks a suite below the NEW divider in the dashboard so the current batch stands out.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class NewBatchAttribute : Attribute { }
