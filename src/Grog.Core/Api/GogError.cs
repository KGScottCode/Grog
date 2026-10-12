using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Grog.Core.Api;

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

    public static bool IsAuthFailure(Exception ex) =>
        ex is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden };
}

public sealed class GogUnavailableException : Exception
{
    public string Reason { get; }
    public int StatusCode { get; }

    public GogUnavailableException(string reason, int statusCode)
        : base(reason) { Reason = reason; StatusCode = statusCode; }
}
