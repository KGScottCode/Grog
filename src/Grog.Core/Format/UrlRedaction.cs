// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Text.RegularExpressions;

namespace Grog.Core.Format;

// Signed CDN and API links carry their credential in the query string; trace output keeps the path only.
public static class UrlRedaction
{
    private static readonly Regex QueryOfUrl = new(
        @"(https?:(?:\\?/){2}[^\s""'?#]+)\?(?!<redacted>)[^\s""'<>]*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // GOG cloud-storage paths carry the account's numeric user id as a segment.
    private static readonly Regex UserIdSegment = new(
        @"(cloudstorage[^\s""']*?/v\d+/)\d{6,}(/|\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly string Home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);

    public static string Scrub(string text)
    {
        var t = QueryOfUrl.Replace(text, "$1?<redacted>");
        t = UserIdSegment.Replace(t, "$1<uid>$2");
        if (Home.Length > 3 && t.Contains(Home, System.StringComparison.OrdinalIgnoreCase))
            t = t.Replace(Home, "~", System.StringComparison.OrdinalIgnoreCase);
        return t;
    }

    public static System.Action<string>? Wrap(System.Action<string>? sink)
        => sink is null ? null : msg => sink(Scrub(msg));
}
