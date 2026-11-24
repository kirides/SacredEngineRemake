using System.Reflection;

namespace Sacred.GameServer.Web;

/// <summary>
/// The static files of the web UI (<c>Assets/*.css</c>, <c>*.js</c>, <c>*.html</c>, <c>*.svg</c>) are embedded resources of this assembly,
/// so the server stays one deployable file and the pages need no external request. Loaded by name through
/// <see cref="Assembly.GetManifestResourceStream(string)"/> (no reflection over types, trim and AOT safe) and cached.
/// </summary>
internal static class Assets
{
    private static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);

    /// <summary>The text of an asset, without the trailing newline of the file.</summary>
    public static string Text(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;
            using var stream = typeof(Assets).Assembly.GetManifestResourceStream("Assets/" + name)
                ?? throw new FileNotFoundException("embedded asset not found: " + name);
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            return Cache[name] = reader.ReadToEnd().TrimEnd('\n', '\r');
        }
    }

    /// <summary>Fills <c>{{name}}</c> placeholders of a template. Values go in as given: callers pass HTML-encoded text or markup they built.</summary>
    public static string Fill(string template, params (string Name, string Value)[] values)
    {
        // One pass over the template: a value is never searched for placeholders itself (a player name may contain "{{body}}").
        var sb = new System.Text.StringBuilder(template.Length + 256);
        var i = 0;
        while (i < template.Length)
        {
            var open = template.IndexOf("{{", i, StringComparison.Ordinal);
            var close = open < 0 ? -1 : template.IndexOf("}}", open + 2, StringComparison.Ordinal);
            if (close < 0) { sb.Append(template, i, template.Length - i); break; }
            sb.Append(template, i, open - i);
            var name = template.AsSpan(open + 2, close - open - 2);
            var found = false;
            foreach (var (n, v) in values)
                if (name.SequenceEqual(n)) { sb.Append(v); found = true; break; }
            if (!found) sb.Append(template, open, close + 2 - open);
            i = close + 2;
        }
        return sb.ToString();
    }

    /// <summary>The page layout (<c>page.html</c>): head with theme script, title and style, the body and the page script (a <c>script</c> element only when there is one).</summary>
    public static string Page(string title, string style, string body, string script = "", string viewport = "width=device-width, initial-scale=1") =>
        Fill(Text("page.html"), ("viewport", viewport), ("theme", Text("theme-init.js")), ("title", title), ("style", style), ("script", script.Length == 0 ? "" : "<script>" + script + "</script>"), ("body", body));
}
