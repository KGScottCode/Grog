// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.Linq;
using Grog.Core.Auth;
using Grog.Core.Cli;
using Grog.Core.Tests.Framework;

[NewBatch]
[Trait("exitcode")]
public class CliExitTests
{
    [Test] void AuthExpired_MapsToCode4()
        => Assert.Equal(ExitCode.AuthExpired, CliExit.ForException(new AuthExpiredException("x")), "session expired");

    [Test] void Captcha_And_BadCreds_MapToLoginBlocked()
    {
        Assert.Equal(ExitCode.LoginBlocked, CliExit.ForException(new CaptchaRequiredException()), "captcha blocks headless");
        Assert.Equal(ExitCode.LoginBlocked, CliExit.ForException(new InvalidCredentialsException("nope")), "bad creds");
    }

    [Test] void Network_MapsToNetworkError()
        => Assert.Equal(ExitCode.NetworkError, CliExit.ForException(new System.Net.Http.HttpRequestException("down")), "GOG unreachable");

    [Test] void Unknown_MapsToGenericError()
        => Assert.Equal(ExitCode.Error, CliExit.ForException(new InvalidOperationException("boom")), "uncategorized -> generic");

    [Test] void NonInteractiveProvider_ThrowsAuthExpired()
    {
        var provider = new NonInteractiveLoginProvider();
        Assert.Throws<AuthExpiredException>(
            () => provider.AcquireAuthorizationCodeAsync(new Uri("https://auth.gog.com/")), "never prompts unattended");
    }
    [Test] void StorageOffline_IsCode10_AndUnmapped()
    {
        // `backup` returns it itself when files wait for a disconnected drive; no exception maps to it.
        Assert.Equal(10, (int)ExitCode.StorageOffline, "scripts depend on the number");
        var values = Enum.GetValues<ExitCode>().Select(v => (int)v).ToList();
        Assert.Equal(values.Count, values.Distinct().Count(), "every exit code is distinct");
        Assert.Equal(ExitCode.Error, CliExit.ForException(new System.IO.IOException("drive gone")), "an IO failure is still the generic error");
    }
}
