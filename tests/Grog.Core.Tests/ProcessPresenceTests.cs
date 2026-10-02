// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using System;
using System.IO;
using Grog.Core.Storage;
using Grog.Core.Tests.Framework;

/// <summary>App-over-CLI presence (09-08): the lock is the truth, the file is only the message.</summary>
[Trait("storage")]
public class ProcessPresenceTests
{
    [Test] void Holder_IsNull_WhenNobodyHoldsTheLock()
    {
        var dir = Directory.CreateTempSubdirectory("grog-presence-").FullName;
        try
        {
            Assert.Null(ProcessPresence.Holder(dir, ProcessPresence.AppLockName), "no file, no holder");
            File.WriteAllText(Path.Combine(dir, ProcessPresence.AppLockName), "4242\napp\n");   // a crash leftover
            Assert.Null(ProcessPresence.Holder(dir, ProcessPresence.AppLockName), "an unlocked leftover file is not a holder");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test] void Hold_IsSeen_ByAnotherProbe_AndReleasedOnDispose()
    {
        var dir = Directory.CreateTempSubdirectory("grog-presence-").FullName;
        try
        {
            using (var app = ProcessPresence.TryHold(dir, ProcessPresence.AppLockName, "app"))
            {
                Assert.True(app.Held, "first holder takes it");
                var seen = ProcessPresence.Holder(dir, ProcessPresence.AppLockName);
                Assert.True(seen is { } h && h.Pid == Environment.ProcessId && h.Note == "app", "the probe reads pid and note");
                using var second = ProcessPresence.TryHold(dir, ProcessPresence.AppLockName, "app");
                Assert.False(second.Held, "a second holder is refused while the first lives");
            }
            Assert.Null(ProcessPresence.Holder(dir, ProcessPresence.AppLockName), "released with the holder");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test] void Disposing_A_Refused_Instance_Leaves_The_Holders_Files()
    {
        var dir = Directory.CreateTempSubdirectory("grog-presence-").FullName;
        try
        {
            using var first = ProcessPresence.TryHold(dir, ProcessPresence.CliLockName, "backup");
            Assert.True(first.Held, "first holder takes it");
            var second = ProcessPresence.TryHold(dir, ProcessPresence.CliLockName, "verify");
            Assert.False(second.Held, "second is refused");
            second.Dispose();
            Assert.True(File.Exists(first.Path), "the refused instance does not unlink the holder's lock");
            Assert.True(File.Exists(first.Path + ".info"), "nor its info sidecar");
            var seen = ProcessPresence.Holder(dir, ProcessPresence.CliLockName);
            Assert.True(seen is { } h && h.Note == "backup", "the first holder is still seen");
        }
        finally { Directory.Delete(dir, true); }
    }

    [Test] void BlockedNote_IsTakenOnce()
    {
        var dir = Directory.CreateTempSubdirectory("grog-presence-").FullName;
        try
        {
            Assert.Null(ProcessPresence.TakeBlockedNote(dir), "nothing yet");
            ProcessPresence.WriteBlockedNote(dir, "backup");
            Assert.Equal("backup", ProcessPresence.TakeBlockedNote(dir), "the verb comes back");
            Assert.Null(ProcessPresence.TakeBlockedNote(dir), "and is consumed");
        }
        finally { Directory.Delete(dir, true); }
    }
}
