// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Format;

/// <summary>Counted nouns, one way: "1 file", "3 files". English only (the UI is English only).</summary>
public static class Plural
{
    /// <summary>"<paramref name="n"/> <paramref name="word"/>", pluralised with "s" or <paramref name="plural"/>.</summary>
    public static string Of(int n, string word, string? plural = null) => $"{n} {Noun(n, word, plural)}";
    /// <summary>The noun alone, for a count that sits elsewhere in the sentence ("3 more files").</summary>
    public static string Noun(int n, string word, string? plural = null) => n == 1 ? word : plural ?? word + "s";
    /// <summary>The bare suffix: "" for one, "s" otherwise.</summary>
    public static string S(int n) => n == 1 ? "" : "s";
}
