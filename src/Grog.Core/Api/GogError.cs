using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Grog.Core.Api;

/// <summary>Turns raw network/GOG exceptions into short, actionable messages so neither the GUI nor the
/// CLI ever shows a stack-trace-flavored string like "An error occurred while sending the request."</summary>
public static class GogError
{
    public static string Describe(Exception ex) => ex switch
    {
        TaskCanceledException or OperationCanceledException
            => "GOG didn't respond in time. Check your connection and try again.",
        HttpRequestException { StatusCode: { } sc } => DescribeStatus((int)sc),
        HttpRequestException => "Can't reach GOG. Check your internet connection and try again.",
        _ => string.IsNullOrWhiteSpace(ex.Message) ? "Something went wrong. Try again." : ex.Message,
    };

    private static string DescribeStatus(int code) => code switch
    {
        401 or 403 => "Your GOG session has expired. Please reconnect your account.",
        429 => "GOG is rate-limiting requests. Wait a moment and try again.",
        408 => "GOG timed out. Try again.",
        404 => "GOG couldn't find that resource (404).",
        >= 500 => $"GOG's servers returned an error ({code}). Try again shortly.",
        _ => $"GOG returned an unexpected response ({code}). Try again.",
    };

    /// <summary>True when the failure looks like an expired/invalid session, so callers can prompt a reconnect.</summary>
    public static bool IsAuthFailure(Exception ex) =>
        ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden };
}

/// <summary>Entitlement refusal: GOG lists the file but will not serve it to this account -- not transport
/// failure, not corruption. Retrying cannot change the answer, so the pipeline must not count retry strikes
/// or mark the file Corrupt. Carries GOG's own wording for display.</summary>
public sealed class GogUnavailableException : Exception
{
    /// <summary>GOG's own explanation, already trimmed for display. Never null or empty.</summary>
    public string Reason { get; }
    public int StatusCode { get; }

    public GogUnavailableException(string reason, int statusCode)
        : base(reason) { Reason = reason; StatusCode = statusCode; }
}
