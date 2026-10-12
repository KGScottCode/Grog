// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System.Collections.Generic;
using Grog.Core.Api;
using Grog.Core.Format;
using Grog.Core.Tests.Framework;

namespace Grog.Core.Tests;

public class UrlRedactionTests
{
    [Test]
    public void Query_string_is_redacted_path_kept()
    {
        var s = UrlRedaction.Scrub("cdn url = https://gog-cdn.example.com/a/setup.exe?token=abc&expires=1 done");
        Assert.Equal("cdn url = https://gog-cdn.example.com/a/setup.exe?<redacted> done", s, "query gone");
    }

    [Test]
    public void Json_escaped_url_is_redacted()
    {
        var s = UrlRedaction.Scrub("{\"downlink\":\"https:\\/\\/cdn.gog.com\\/x.bin?sig=SECRET\"}");
        Assert.False(s.Contains("SECRET"), "escaped url query gone");
    }

    [Test]
    public void Cloud_storage_user_id_segment_is_redacted()
    {
        var s = UrlRedaction.Scrub("GET https://cloudstorage.gog.com/v1/48123456789012345/51234567/x.sav");
        Assert.Equal("GET https://cloudstorage.gog.com/v1/<uid>/51234567/x.sav", s, "uid gone, clientId kept");
    }

    [Test]
    public void Home_directory_becomes_tilde()
    {
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        Assert.SkipUnless(home.Length > 3, "no user profile directory in this environment");
        var s = UrlRedaction.Scrub("saved to " + System.IO.Path.Combine(home, "Grog", "x.json"));
        Assert.False(s.Contains(home), "home path gone");
        Assert.True(s.Contains("~"), "tilde present");
    }

    [Test]
    public void Scrub_is_idempotent()
    {
        var once = UrlRedaction.Scrub("https://cdn.gog.com/f.exe?token=SECRET");
        Assert.Equal(once, UrlRedaction.Scrub(once), "second pass changes nothing");
    }

    [Test]
    public void Text_without_query_is_unchanged()
    {
        const string t = "resolve: GET https://api.gog.com/products/1/downlink/installer/en1";
        Assert.Equal(t, UrlRedaction.Scrub(t), "no query, no change");
    }

    [Test]
    public void Api_client_trace_sink_never_sees_a_query()
    {
        var seen = new List<string>();
        using var http = new System.Net.Http.HttpClient();
        var api = new GogApiClient(http, null!);
        api.DebugTrace = seen.Add;
        api.DebugTrace!("final=https://cdn.gog.com/f.exe?token=SECRET");
        Assert.False(seen[0].Contains("SECRET"), "sink got the scrubbed line");
    }
}
