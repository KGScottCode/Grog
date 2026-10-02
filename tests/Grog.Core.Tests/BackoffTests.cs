// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Grog.Core.Api;
using Grog.Core.Auth;
using Grog.Core.Tests.Framework;

/// <summary>
/// The 429/5xx backoff (design agreed): per-run, in-memory, exponential 2s doubling to a
/// 5-minute cap, Retry-After honored, one log line per wait. 429 retries as long as GOG keeps saying
/// wait; a 5xx is retried a few times then SURFACED - a permanently broken endpoint must fail loudly.
/// Transport failures are retried per call with a fixed 1 s pause, never through the shared schedule.
/// The delay seam replaces real waiting so these run in milliseconds.
/// </summary>
[NewBatch]
[Trait("api")]
public sealed class BackoffTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        public Queue<Func<HttpResponseMessage>> Responses { get; } = new();
        public int Sent;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent++;
            return Task.FromResult(Responses.Count > 0 ? Responses.Dequeue()() : Ok());
        }
    }

    private sealed class NoAuth : IAuthService
    {
        public bool HasStoredSession => true;
        public Task<AuthSession> EnsureAuthenticatedAsync(CancellationToken ct = default)
            => Task.FromResult(new AuthSession("at", "rt", DateTimeOffset.UtcNow.AddHours(1), "u"));
        public Task SignOutAsync() => Task.CompletedTask;
    }

    private static HttpResponseMessage Ok() => new(HttpStatusCode.OK)
    { Content = new StringContent("{\"title\":\"G\",\"game_type\":\"game\"}") };

    private static HttpResponseMessage Throttle(int? retryAfterSeconds = null)
    {
        var r = new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("") };
        if (retryAfterSeconds is { } s) r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(s));
        return r;
    }

    private static (GogApiClient Api, FakeHandler Handler, List<TimeSpan> Delays, List<string> Log) Client()
    {
        var handler = new FakeHandler();
        var api = new GogApiClient(new HttpClient(handler), new NoAuth());
        var delays = new List<TimeSpan>();
        var log = new List<string>();
        api.DelayAsync = (d, _) => { delays.Add(d); return Task.CompletedTask; };
        api.OnThrottle = log.Add;
        return (api, handler, delays, log);
    }

    [Test]
    public async Task A_429_is_retried_after_an_exponential_wait_and_logged_once_per_wait()
    {
        var (api, h, delays, log) = Client();
        h.Responses.Enqueue(() => Throttle());
        h.Responses.Enqueue(() => Throttle());
        h.Responses.Enqueue(Ok);

        var info = await api.GetProductInfoAsync(1);

        Assert.True(info is not null, "the request eventually succeeds");
        Assert.Equal(3, h.Sent, "two throttles, one success");
        Assert.Equal(2, delays.Count(d => d >= TimeSpan.FromSeconds(2)), "each throttle waited");
        Assert.True(delays[1] > delays[0], "the second wait is longer (exponential)");
        Assert.Equal(2, log.Count, "one line per wait");
    }

    [Test]
    public async Task Retry_After_is_honored_over_the_exponential_schedule()
    {
        var (api, h, delays, _) = Client();
        h.Responses.Enqueue(() => Throttle(retryAfterSeconds: 30));
        h.Responses.Enqueue(Ok);

        await api.GetProductInfoAsync(1);

        Assert.Equal(TimeSpan.FromSeconds(30), delays[0], "the server's number wins");
    }

    [Test]
    public async Task A_persistent_5xx_is_surfaced_after_a_few_retries_not_retried_forever()
    {
        var (api, h, delays, _) = Client();
        for (var i = 0; i < 20; i++)
            h.Responses.Enqueue(() => new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("") });

        try { await api.GetProductInfoAsync(1); Assert.True(false, "a permanent 500 must surface as an error"); }
        catch (HttpRequestException) { /* expected: the caller's error path sees the 500 */ }
        Assert.True(h.Sent <= 6, $"bounded retries, not a spin (sent {h.Sent})");
    }

    [Test]
    public async Task A_success_resets_the_strike_count()
    {
        var (api, h, delays, _) = Client();
        h.Responses.Enqueue(() => Throttle());
        h.Responses.Enqueue(Ok);      // resets strikes
        h.Responses.Enqueue(() => Throttle());
        h.Responses.Enqueue(Ok);

        await api.GetProductInfoAsync(1);
        await api.GetProductInfoAsync(2);

        Assert.Equal(2, delays.Count, "two waits total");
        Assert.Equal(delays[0], delays[1], "the second run starts back at the base delay");
    }

    [Test]
    public async Task A_transport_failure_is_retried_locally_with_a_fixed_pause_then_succeeds()
    {
        // A dropped connection or a client timeout gets a fixed 1 s pause, not the exponential schedule.
        var (api, h, delays, log) = Client();
        h.Responses.Enqueue(() => throw new HttpRequestException("connection reset"));
        h.Responses.Enqueue(() => throw new TaskCanceledException("timeout", new TimeoutException("client timeout")));
        h.Responses.Enqueue(Ok);

        var info = await api.GetProductInfoAsync(1);

        Assert.True(info is not null, "the third attempt succeeds");
        Assert.Equal(3, h.Sent, "two failures, one success");
        Assert.Equal(2, delays.Count, "each failure waited");
        Assert.True(delays.All(d => d == TimeSpan.FromSeconds(1)), "a fixed 1 s pause each time");
        Assert.Equal(2, log.Count, "one line per wait");
    }

    [Test]
    public async Task A_persistent_transport_failure_surfaces_after_three_tries()
    {
        var (api, h, delays, _) = Client();
        for (var i = 0; i < 20; i++)
            h.Responses.Enqueue(() => throw new HttpRequestException("connection reset"));

        try { await api.GetProductInfoAsync(1); Assert.True(false, "a dead connection must surface"); }
        catch (HttpRequestException) { /* expected */ }
        Assert.Equal(3, h.Sent, $"three tries, not a spin (sent {h.Sent})");
        Assert.Equal(2, delays.Count, "a pause between tries, none after the last");
    }

    [Test]
    public async Task Transport_failures_never_touch_the_throttle_strikes_or_the_shared_gate()
    {
        var (api, h, delays, _) = Client();
        for (var i = 0; i < 3; i++)
            h.Responses.Enqueue(() => throw new HttpRequestException("connection reset"));
        h.Responses.Enqueue(() => Throttle());
        h.Responses.Enqueue(Ok);

        try { await api.GetProductInfoAsync(1); } catch (HttpRequestException) { /* expected */ }
        Assert.Equal(0, api.ThrottleStrikes, "an offline machine is not a throttled one");
        Assert.True(delays.All(d => d == TimeSpan.FromSeconds(1)), "no gate wait was queued for the next call");

        delays.Clear();
        await api.GetProductInfoAsync(2);
        Assert.Equal(TimeSpan.FromSeconds(2), delays[0], "the next throttle starts at the base delay");
    }

    [Test]
    public async Task A_cancelled_caller_token_is_not_retried()
    {
        var (api, h, delays, _) = Client();
        using var cts = new CancellationTokenSource();
        h.Responses.Enqueue(() => { cts.Cancel(); throw new OperationCanceledException(cts.Token); });
        h.Responses.Enqueue(Ok);

        try { await api.GetProductInfoAsync(1, cts.Token); Assert.True(false, "the cancel must propagate"); }
        catch (OperationCanceledException) { /* expected */ }
        Assert.Equal(1, h.Sent, "no second attempt after the caller cancelled");
        Assert.Empty(delays, "and no wait");
    }

    [Test]
    public async Task The_wait_is_capped_at_five_minutes()
    {
        var (api, h, delays, _) = Client();
        h.Responses.Enqueue(() => Throttle(retryAfterSeconds: 3600));
        h.Responses.Enqueue(Ok);

        await api.GetProductInfoAsync(1);

        Assert.Equal(TimeSpan.FromMinutes(5), delays[0], "even the server's number is capped");
    }
}
