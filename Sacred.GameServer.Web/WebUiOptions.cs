using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Sacred.GameServer.Web;

/// <summary>
/// Settings of the web UI, read the .NET way from <c>WebUi:*</c> keys. Sources, lowest to highest priority: environment variables
/// (<c>SACRED_GS_WebUi__Port</c>), the JSON file (<c>webui.json</c> or <c>--webui-config=path</c>, optional), the command line
/// (<c>--WebUi:Port=8080</c>, <c>--WebUi:Port 8080</c> or <c>--webui-port=8080</c>).
/// </summary>
public sealed class WebUiOptions
{
    public const string EnvironmentPrefix = "SACRED_GS_";
    public bool Enabled { get; init; }
    /// <summary>Bind address or host name: an IP, "localhost", a DNS name, or "*" for every interface.</summary>
    public string Hostname { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 8080;
    /// <summary>Admin password (HTTP Basic with any user name, or a Bearer token). Required unless the UI only listens on loopback.</summary>
    public string? Password { get; init; }
    /// <summary>Origins that may call the API from a browser (CORS). "*" allows all (only sensible with a password); empty: same origin only.</summary>
    public string[] CorsOrigins { get; init; } = [];
    /// <summary>Who may put the page in a frame: empty/"none" (default), "self", "*" or a list of origins. Sent as CSP frame-ancestors.</summary>
    public string[] FrameAncestors { get; init; } = [];
    /// <summary>Host names the server answers to (Host header check). Empty on loopback binds: localhost, 127.0.0.1 and the hostname.</summary>
    public string[] AllowedHosts { get; init; } = [];
    /// <summary>Serve the page from this directory instead of the embedded copy (to customise it or embed it into a site).</summary>
    public string? StaticDirectory { get; init; }
    /// <summary>URL prefix when the UI sits behind a reverse proxy path (for example /sacred).</summary>
    public string PathBase { get; init; } = "";

    public static IConfigurationRoot BuildConfiguration(string[] args)
    {
        var path = "webui.json";
        var explicitPath = false;
        for (var i = 0; i < args.Length; i++)
        {
            const string key = "--webui-config";
            if (args[i].StartsWith(key + "=", StringComparison.OrdinalIgnoreCase)) { path = args[i][(key.Length + 1)..]; explicitPath = true; }
            else if (args[i].Equals(key, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) { path = args[++i]; explicitPath = true; }
        }
        return new ConfigurationBuilder()
            .AddEnvironmentVariables(EnvironmentPrefix)
            .AddJsonFile(Path.GetFullPath(path), optional: !explicitPath, reloadOnChange: false)
            .AddCommandLine(args.Where(a => !(a.StartsWith('-') && !a.StartsWith("--", StringComparison.Ordinal))).ToArray(), SwitchMappings)
            .Build();
    }

    private static readonly Dictionary<string, string> SwitchMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["--webui-enabled"] = "WebUi:Enabled", ["--webui-hostname"] = "WebUi:Hostname", ["--webui-port"] = "WebUi:Port",
        ["--webui-password"] = "WebUi:Password", ["--webui-cors-origins"] = "WebUi:CorsOrigins",
        ["--webui-frame-ancestors"] = "WebUi:FrameAncestors", ["--webui-allowed-hosts"] = "WebUi:AllowedHosts",
        ["--webui-static-directory"] = "WebUi:StaticDirectory", ["--webui-path-base"] = "WebUi:PathBase",
    };

    public static WebUiOptions From(IConfiguration configuration)
    {
        var section = configuration.GetSection("WebUi");
        var port = int.TryParse(section["Port"], NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 8080;
        var pathBase = (section["PathBase"] ?? "").Trim().TrimEnd('/');
        if (pathBase.Length > 0 && pathBase[0] != '/') pathBase = "/" + pathBase;
        return new WebUiOptions
        {
            Enabled = Bool(section["Enabled"]),
            Hostname = string.IsNullOrWhiteSpace(section["Hostname"]) ? "127.0.0.1" : section["Hostname"]!.Trim(),
            Port = Math.Clamp(port, 0, 65535),
            Password = string.IsNullOrEmpty(section["Password"]) ? null : section["Password"],
            CorsOrigins = List(section, "CorsOrigins"),
            FrameAncestors = List(section, "FrameAncestors"),
            AllowedHosts = List(section, "AllowedHosts"),
            StaticDirectory = string.IsNullOrWhiteSpace(section["StaticDirectory"]) ? null : section["StaticDirectory"],
            PathBase = pathBase,
        };
    }

    /// <summary>A bare switch (<c>--webui-enabled</c>) arrives as "true" through the command line provider.</summary>
    private static bool Bool(string? value) => value is not null && (value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");

    /// <summary>A list is a comma/semicolon separated string or an indexed section (JSON array, <c>WebUi:CorsOrigins:0=...</c>).</summary>
    private static string[] List(IConfigurationSection section, string key)
    {
        var items = new List<string>();
        var child = section.GetSection(key);
        if (child.Value is { } text) items.AddRange(text.Split([',', ';'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        foreach (var entry in child.GetChildren())
            if (entry.Value is { Length: > 0 } v) items.Add(v.Trim());
        return items.ToArray();
    }
}
