// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using Grog.Core.Auth;

namespace Grog.Core.Cli;

/// <summary>CLI process exit codes; 0 is success, each non-zero value is a distinct actionable condition.
/// Values are stable -- scripts depend on these numbers.</summary>
public enum ExitCode
{
    Success = 0,            // everything did what it should
    Error = 1,             // generic / unexpected failure
    UsageError = 2,        // bad arguments or flags
    NotConfigured = 3,     // nothing to run against yet (e.g. no backup location) -- needs setup
    AuthExpired = 4,       // GOG session expired; a one-time interactive login is needed
    LoginBlocked = 5,      // headless login hit reCAPTCHA / 2-step -- can't proceed unattended
    NetworkError = 6,      // GOG unreachable / transient network problem -- safe to retry later
    InsufficientSpace = 7, // the backup won't fit and can't spill -- add or free space
    IntegrityFailed = 8,   // verification found corrupt or missing files
    AppRunning = 9,        // the Grog app is open and owns the profile; a writer verb refused to run (09-08)
    StorageOffline = 10,   // files are waiting for a disconnected drive and stay queued -- reconnect it and re-run
}

/// <summary>Maps an exception to the exit code a scheduled caller should see. Pure and testable.</summary>
public static class CliExit
{
    public static ExitCode ForException(Exception ex) => ex switch
    {
        AuthExpiredException => ExitCode.AuthExpired,
        CaptchaRequiredException => ExitCode.LoginBlocked,
        InvalidCredentialsException => ExitCode.LoginBlocked,
        System.Net.Http.HttpRequestException => ExitCode.NetworkError,
        System.Net.Sockets.SocketException => ExitCode.NetworkError,
        _ => ExitCode.Error,
    };
}
