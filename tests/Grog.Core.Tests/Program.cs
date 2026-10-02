// Grog.Core.Tests entry point -- same shape as MRServer.Tests: Main runs the framework
// (TestRunner) with an optional trait filter, e.g. `dotnet run -- integration` to run only
// the live-GOG-API suite. F5 with this project as startup runs the whole dashboard.
// Exit code 0 iff nothing failed or errored (skips are fine) -- CI gates on this.
// Licensed under GPL-3.0-or-later
namespace Grog.Core.Tests;

using Grog.Core.Tests.Framework;

public static class Program
{
    public static int Main(string[] args)
    {
        // Never touch the real %APPDATA%/Grog during tests: point config at a throwaway temp dir.
        var cfg = System.IO.Directory.CreateTempSubdirectory("grog-test-cfg-").FullName;
        System.Environment.SetEnvironmentVariable("GROG_CONFIG_DIR", cfg);

        string? filter = args.Length > 0 ? args[0] : null;

        int framework = TestRunner.Run(filter);

        try { System.IO.Directory.Delete(cfg, true); } catch { }

        System.Console.WriteLine();
        System.Console.WriteLine(framework == 0 ? "ALL TESTS PASS" : "TEST FAILURE");
        return framework;
    }
}
