// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Cli;

/// <summary>
/// Normalizes argv so the command name is always args[0].
/// <para>
/// Help promises "global flags (any command): --silent / --json / --yes / --root &lt;path&gt;", but the
/// dispatcher takes the command as args[0] unconditionally, so <c>grog --json report</c> died with
/// "Unknown command '--json'". Rather than teach every call site about leading flags, we rotate any
/// leading run of flags to the END of argv. Everything downstream (which scans argv positionally-agnostic
/// via Contains/GetArgValue) then behaves exactly as if the user had typed them after the command.
/// </para>
/// <para>
/// The one subtlety: GetArgValue's space form is greedy (it captures up to the next --flag), so a leading
/// <c>--root /tmp/x report</c> would otherwise swallow "report" as part of the path. Moving the flag run
/// to the tail -- with only its own value words attached -- keeps that capture correct.
/// </para>
/// </summary>
public static class ArgOrder
{
    /// <summary>Flags that take a value in the space-separated form (<c>--root /tmp/x</c>). Anything not
    /// listed is a boolean switch, so the token after it is the command, not a value.</summary>
    private static readonly HashSet<string> ValueFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--blacklist","--client","--concurrency","--depth","--extra-type","--id","--label","--lang",
        "--largest","--limit","--limit-mbps","--mode","--os","--parallel","--primary","--role","--root",
        "--secondary","--title","--title-regex","--to","--type","--with",
    };

    public static string[] CommandFirst(string[] args)
    {
        if (args.Length == 0 || !args[0].StartsWith("--")) return args;

        var leading = new List<string>();
        int i = 0;
        while (i < args.Length && args[i].StartsWith("--"))
        {
            var tok = args[i++];
            leading.Add(tok);
            // --name=value is self-contained; only the space form pulls in following words.
            if (tok.Contains('=') || !ValueFlags.Contains(tok)) continue;
            // Exactly ONE value word before the command -- the next non-flag token is the command name, so a
            // greedy multi-word capture here would eat it. Multi-word values placed before the command must
            // use the --name=value form (after the command, the greedy form still works as before).
            if (i < args.Length && !args[i].StartsWith("--")) leading.Add(args[i++]);
        }

        // Nothing but flags: leave argv alone so the caller falls through to help/usage as before.
        if (i >= args.Length) return args;

        var rest = new List<string>(args.Length);
        for (int j = i; j < args.Length; j++) rest.Add(args[j]);
        rest.AddRange(leading);
        return rest.ToArray();
    }
}
