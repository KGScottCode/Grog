// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using Grog.Core.Tests.Framework;

namespace Grog.Core.Tests;

public class BackoffPolicyTests
{
    [Test]
    public void Exponential_from_base_clamped_to_max()
    {
        var b = TimeSpan.FromSeconds(2); var m = TimeSpan.FromSeconds(60);
        Assert.Equal(TimeSpan.FromSeconds(2), Grog.Core.Api.BackoffPolicy.Delay(1, b, m), "first retry = base");
        Assert.Equal(TimeSpan.FromSeconds(8), Grog.Core.Api.BackoffPolicy.Delay(3, b, m), "doubles");
        Assert.Equal(m, Grog.Core.Api.BackoffPolicy.Delay(20, b, m), "capped");
    }

    [Test]
    public void Server_hint_wins_but_is_clamped()
    {
        var b = TimeSpan.FromSeconds(2); var m = TimeSpan.FromSeconds(60);
        Assert.Equal(TimeSpan.FromSeconds(7), Grog.Core.Api.BackoffPolicy.Delay(1, b, m, TimeSpan.FromSeconds(7)), "hint");
        Assert.Equal(b, Grog.Core.Api.BackoffPolicy.Delay(1, b, m, TimeSpan.Zero), "zero hint cannot hot-loop");
        Assert.Equal(m, Grog.Core.Api.BackoffPolicy.Delay(1, b, m, TimeSpan.FromHours(1)), "huge hint capped");
    }
}
