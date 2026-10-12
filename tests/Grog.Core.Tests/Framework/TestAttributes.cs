// Grog test framework -- attribute set matching the MRServer.Tests.Framework contract,
// so muscle memory transfers 1:1 between projects.
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests.Framework;

using System;

[AttributeUsage(AttributeTargets.Method)]
public sealed class TestAttribute : Attribute
{
    public string? Name { get; }
    public int TimeoutSeconds { get; set; }
    public TestAttribute(string? name = null) => Name = name;
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class SetupAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method)]
public sealed class TeardownAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true)]
public sealed class TraitAttribute : Attribute
{
    public string Category { get; }
    public TraitAttribute(string category) => Category = category;
}

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class SkipAttribute : Attribute
{
    public string Reason { get; }
    public SkipAttribute(string reason) => Reason = reason;
}

[AttributeUsage(AttributeTargets.Class)]
public sealed class NewBatchAttribute : Attribute { }
