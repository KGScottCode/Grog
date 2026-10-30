// Grog test framework -- fake HTTP handlers hand failures back the way HttpClient does: as a faulted task.
// A throw inside SendAsync is caught first by HttpClient (external code), which the VS debugger reports as
// "user-unhandled" and stops on, although the code under test handles it.
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests.Framework;

using System;
using System.Net.Http;
using System.Threading.Tasks;

public static class Faults
{
    public static Task<HttpResponseMessage> AsTask(Func<HttpResponseMessage> make)
    {
        try { return Task.FromResult(make()); }
        catch (Exception ex) { return Task.FromException<HttpResponseMessage>(ex); }
    }
}
