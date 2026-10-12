// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Format;

public static class Plural
{
    public static string Of(int n, string word, string? plural = null) => $"{n} {Noun(n, word, plural)}";
    public static string Noun(int n, string word, string? plural = null) => n == 1 ? word : plural ?? word + "s";
    public static string S(int n) => n == 1 ? "" : "s";
}
