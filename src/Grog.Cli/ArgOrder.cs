// Grog - GOG.com library backup tool
// Licensed under GPL-3.0-or-later
namespace Grog.Cli;

public static class ArgOrder
{
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
            if (tok.Contains('=') || !ValueFlags.Contains(tok)) continue;
            if (i < args.Length && !args[i].StartsWith("--")) leading.Add(args[i++]);
        }

        if (i >= args.Length) return args;

        var rest = new List<string>(args.Length);
        for (int j = i; j < args.Length; j++) rest.Add(args[j]);
        rest.AddRange(leading);
        return rest.ToArray();
    }
}
