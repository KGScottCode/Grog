// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;

namespace Grog.Core.Auth;

public sealed class AuthExpiredException : Exception
{
    public AuthExpiredException(string message) : base(message) { }
}
