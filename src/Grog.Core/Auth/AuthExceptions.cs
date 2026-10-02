// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;

namespace Grog.Core.Auth;

/// <summary>A fresh interactive login is needed but the run is non-interactive; lets the caller exit
/// cleanly with a distinct code instead of hanging on a browser no one will complete.</summary>
public sealed class AuthExpiredException : Exception
{
    public AuthExpiredException(string message) : base(message) { }
}

public sealed class CaptchaRequiredException : Exception
{
    public CaptchaRequiredException()
        : base("GOG is requiring a CAPTCHA, which cannot be completed from a scripted login. " +
               "Use the browser login instead.") { }
}

public sealed class InvalidCredentialsException : Exception
{
    public InvalidCredentialsException(string message) : base(message) { }
}

public sealed class LoginFlowChangedException : Exception
{
    public LoginFlowChangedException(string what)
        : base($"GOG's login page has changed ({what}). The scripted login needs updating; " +
               "use the browser login in the meantime.") { }
}
