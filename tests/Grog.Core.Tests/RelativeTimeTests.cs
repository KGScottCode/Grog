// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using Grog.Core.Format;
using Grog.Core.Tests.Framework;

namespace Grog.Core.Tests;

public class RelativeTimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 2, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void Span_walks_the_unit_table()
    {
        Assert.Equal("1 minute", RelativeTime.Span(TimeSpan.FromSeconds(20)), "floors to a minute");
        Assert.Equal("6 hours", RelativeTime.Span(TimeSpan.FromHours(6)), "hours");
        Assert.Equal("1 day", RelativeTime.Span(TimeSpan.FromHours(30)), "singular day");
        Assert.Equal("2 months", RelativeTime.Span(TimeSpan.FromDays(70)), "months");
        Assert.Equal("1 year", RelativeTime.Span(TimeSpan.FromDays(400)), "years");
    }

    [Test]
    public void Ago_and_Old_share_the_table()
    {
        Assert.Equal("Never", RelativeTime.Ago(null, Now), "null");
        Assert.Equal("just now", RelativeTime.Ago(Now.AddSeconds(-5), Now), "under a minute");
        Assert.Equal("3 days ago", RelativeTime.Ago(Now.AddDays(-3), Now), "days ago");
        Assert.Equal("minutes old", RelativeTime.Old(Now.AddMinutes(-20), Now), "young age");
        Assert.Equal("3 days old", RelativeTime.Old(Now.AddDays(-3), Now), "days old");
    }
}
