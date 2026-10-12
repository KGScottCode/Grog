// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
using System;
using Grog.Core.Auth;

namespace Grog.Core.Cli;

public enum ExitCode
{
    Success = 0,           
    Error = 1,             
    UsageError = 2,        
    NotConfigured = 3,     
    AuthExpired = 4,       
    LoginBlocked = 5,      
    NetworkError = 6,      
    InsufficientSpace = 7, 
    IntegrityFailed = 8,   
    AppRunning = 9,        
    StorageOffline = 10,   
}

public static class CliExit
{
    public static ExitCode ForException(Exception ex) => ex switch
    {
        AuthExpiredException => ExitCode.AuthExpired,
        System.Net.Http.HttpRequestException => ExitCode.NetworkError,
        System.Net.Sockets.SocketException => ExitCode.NetworkError,
        _ => ExitCode.Error,
    };
}
