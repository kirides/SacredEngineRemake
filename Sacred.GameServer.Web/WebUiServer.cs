using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Sacred.GameServer.Web;

/// <summary>
/// The management UI: one embedded page plus a small JSON API under <c>/api</c>. Plain request delegate with a path switch (no
/// routing/reflection), source generated JSON, so it stays trim and AOT clean. Chat is delivered by long polling (<c>/api/chat</c>),
/// which also works cross-origin with an Authorization header, unlike EventSource.
/// </summary>
public sealed class WebUiServer : IAsyncDisposable
{
    private const int MaxText = 200, MaxLongPollSeconds = 25;
    private readonly WebUiOptions _options;
    private readonly IServerAdmin _admin;
    private readonly Action<string> _log;
    private readonly byte[] _passwordHash;
    private WebApplication? _app;
    private byte[]? _indexPage;

    public WebUiServer(WebUiOptions options, IServerAdmin admin, Action<string> log)
    {
        _options = options; _admin = admin; _log = log;
        _passwordHash = options.Password is null ? [] : SHA256.HashData(Encoding.UTF8.GetBytes(options.Password));
    }

    /// <summary>The ports Kestrel really bound (useful with Port 0 in tests).</summary>
    public IReadOnlyList<int> Ports { get; private set; } = [];

    /// <summary>Why the UI must not start with these options, or null.</summary>
    public static string? Validate(WebUiOptions options)
    {
        if (options.Password is null && !IsLoopbackOnly(options.Hostname))
            return $"WebUi:Password is required when WebUi:Hostname ('{options.Hostname}') is not a loopback address.";
        return null;
    }

    private static bool IsLoopbackOnly(string hostname) =>
        hostname.Equals("localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(hostname, out var ip) && IPAddress.IsLoopback(ip));

    public async Task StartAsync()
    {
        if (Validate(_options) is { } problem) throw new InvalidOperationException(problem);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = 16 * 1024;
            kestrel.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            kestrel.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            foreach (var address in ResolveBindAddresses(_options.Hostname))
                kestrel.Listen(address, _options.Port, listen => listen.Protocols = HttpProtocols.Http1AndHttp2);
        });
        var app = builder.Build();
        app.Run(Handle);
        await app.StartAsync().ConfigureAwait(false);
        _app = app;
        var ports = new List<int>();
        foreach (var address in app.Urls) if (Uri.TryCreate(address, UriKind.Absolute, out var uri)) ports.Add(uri.Port);
        Ports = ports;
        _log($"Web UI on http://{_options.Hostname}:{(ports.Count > 0 ? ports[0] : _options.Port)}{_options.PathBase}/ ({(_options.Password is null ? "no password, loopback only" : "password protected")})");
    }

    private static IEnumerable<IPAddress> ResolveBindAddresses(string hostname)
    {
        if (hostname is "*" or "+" or "0.0.0.0" or "::") return [hostname == "0.0.0.0" ? IPAddress.Any : IPAddress.IPv6Any];
        if (hostname.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return [IPAddress.Loopback, IPAddress.IPv6Loopback];
        if (IPAddress.TryParse(hostname, out var ip)) return [ip];
        return Dns.GetHostAddresses(hostname);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) { await _app.StopAsync().ConfigureAwait(false); await _app.DisposeAsync().ConfigureAwait(false); }
    }

    // ---- request handling -------------------------------------------------------------------------------------------

    private async Task Handle(HttpContext context)
    {
        var request = context.Request; var response = context.Response;
        SetSecurityHeaders(response);
        if (!HostAllowed(request.Host.Host)) { response.StatusCode = StatusCodes.Status400BadRequest; return; }
        var origin = request.Headers.Origin.ToString();
        var corsAllowed = origin.Length > 0 && OriginAllowed(origin);
        if (corsAllowed)
        {
            response.Headers.AccessControlAllowOrigin = origin;
            response.Headers.Vary = "Origin";
            response.Headers.AccessControlAllowHeaders = "Authorization, Content-Type";
            response.Headers.AccessControlAllowMethods = "GET, POST, OPTIONS";
            response.Headers.AccessControlMaxAge = "600";
        }
        if (HttpMethods.IsOptions(request.Method)) { response.StatusCode = corsAllowed ? StatusCodes.Status204NoContent : StatusCodes.Status403Forbidden; return; }
        // A foreign origin that is not allowed must not drive the API with the browser's cached credentials.
        if (origin.Length > 0 && !corsAllowed && !SameOrigin(origin, request)) { response.StatusCode = StatusCodes.Status403Forbidden; return; }

        if (!Authorized(request))
        {
            response.StatusCode = StatusCodes.Status401Unauthorized;
            response.Headers.WWWAuthenticate = "Basic realm=\"Sacred GameServer\", charset=\"UTF-8\"";
            return;
        }

        var path = request.Path.Value ?? "/";
        if (_options.PathBase.Length > 0)
        {
            if (!path.StartsWith(_options.PathBase, StringComparison.Ordinal)) { response.StatusCode = StatusCodes.Status404NotFound; return; }
            path = path[_options.PathBase.Length..];
            if (path.Length == 0) path = "/";
        }
        if (path.StartsWith("/api/", StringComparison.Ordinal)) await Api(context, path[5..]).ConfigureAwait(false);
        else await Static(context, path).ConfigureAwait(false);
    }

    private void SetSecurityHeaders(HttpResponse response)
    {
        var headers = response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.CacheControl = "no-store";
        string ancestors = FrameAncestors();
        headers.ContentSecurityPolicy = $"default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; connect-src *; img-src 'self' data:; frame-ancestors {ancestors}; base-uri 'none'; form-action 'self'";
        if (ancestors == "'none'") headers.XFrameOptions = "DENY";
        else if (ancestors == "'self'") headers.XFrameOptions = "SAMEORIGIN";
    }

    private string FrameAncestors()
    {
        if (_options.FrameAncestors.Length == 0 || _options.FrameAncestors.Any(a => a.Equals("none", StringComparison.OrdinalIgnoreCase))) return "'none'";
        return string.Join(' ', _options.FrameAncestors.Select(a => a.Equals("self", StringComparison.OrdinalIgnoreCase) ? "'self'" : a));
    }

    private bool HostAllowed(string host)
    {
        if (_options.AllowedHosts.Length > 0)
            return _options.AllowedHosts.Any(a => a == "*" || a.Equals(host, StringComparison.OrdinalIgnoreCase));
        if (_options.Password is not null || !IsLoopbackOnly(_options.Hostname)) return true;
        // Loopback without a password: refuse foreign Host headers (DNS rebinding from a web page).
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host == "127.0.0.1" || host == "[::1]" || host == "::1" ||
               host.Equals(_options.Hostname, StringComparison.OrdinalIgnoreCase);
    }

    private bool OriginAllowed(string origin) =>
        _options.CorsOrigins.Any(o => o == "*" || o.TrimEnd('/').Equals(origin, StringComparison.OrdinalIgnoreCase));

    private static bool SameOrigin(string origin, HttpRequest request) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var uri) && uri.Authority.Equals(request.Host.Value, StringComparison.OrdinalIgnoreCase);

    private bool Authorized(HttpRequest request)
    {
        if (_options.Password is null) return true;
        var header = request.Headers.Authorization.ToString();
        string? candidate = null;
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) candidate = header[7..].Trim();
        else if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
                var colon = decoded.IndexOf(':');
                candidate = colon < 0 ? decoded : decoded[(colon + 1)..];
            }
            catch (FormatException) { return false; }
        }
        if (candidate is null) return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(candidate)), _passwordHash);
    }

    // ---- static page ------------------------------------------------------------------------------------------------

    private async Task Static(HttpContext context, string path)
    {
        var response = context.Response;
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)) { response.StatusCode = StatusCodes.Status405MethodNotAllowed; return; }
        if (path != "/" && path != "/index.html") { response.StatusCode = StatusCodes.Status404NotFound; return; }
        var page = LoadIndex();
        if (page is null) { response.StatusCode = StatusCodes.Status404NotFound; return; }
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength = page.Length;
        if (HttpMethods.IsHead(context.Request.Method)) return;
        await response.Body.WriteAsync(page, context.RequestAborted).ConfigureAwait(false);
    }

    private byte[]? LoadIndex()
    {
        if (_options.StaticDirectory is { } directory)
        {
            var file = Path.Combine(directory, "index.html");
            return File.Exists(file) ? File.ReadAllBytes(file) : null;
        }
        if (_indexPage is not null) return _indexPage;
        using var stream = typeof(WebUiServer).Assembly.GetManifestResourceStream("wwwroot/index.html");
        if (stream is null) return null;
        var buffer = new byte[stream.Length];
        stream.ReadExactly(buffer);
        return _indexPage = buffer;
    }

    // ---- API ----------------------------------------------------------------------------------------------------------

    private async Task Api(HttpContext context, string name)
    {
        var request = context.Request; var response = context.Response;
        if (HttpMethods.IsGet(request.Method))
        {
            switch (name)
            {
                case "overview": await Json(response, _admin.Overview(), WebUiJson.Default.ServerOverview).ConfigureAwait(false); return;
                case "players": await Json(response, _admin.Players().ToArray(), WebUiJson.Default.PlayerInfoArray).ConfigureAwait(false); return;
                case "bans": await Json(response, _admin.Bans().ToArray(), WebUiJson.Default.BanEntryArray).ConfigureAwait(false); return;
                case "chat": await Chat(context).ConfigureAwait(false); return;
            }
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (!HttpMethods.IsPost(request.Method)) { response.StatusCode = StatusCodes.Status405MethodNotAllowed; return; }
        // Only JSON bodies: a cross-site form post cannot send this content type without a CORS preflight.
        if (request.ContentType is null || !request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        { response.StatusCode = StatusCodes.Status415UnsupportedMediaType; return; }
        CommandRequest body;
        try { body = await JsonSerializer.DeserializeAsync(request.Body, WebUiJson.Default.CommandRequest, context.RequestAborted).ConfigureAwait(false) ?? new CommandRequest(); }
        catch (JsonException) { await Fail(response, 400, "invalid JSON").ConfigureAwait(false); return; }
        var (command, error) = Build(name, body);
        if (error is not null) { await Fail(response, 400, error).ConfigureAwait(false); return; }
        if (!_admin.Submit(command)) { await Fail(response, 503, "command queue full").ConfigureAwait(false); return; }
        await Json(response, new CommandResult(true, null), WebUiJson.Default.CommandResult).ConfigureAwait(false);
    }

    private async Task Chat(HttpContext context)
    {
        var query = context.Request.Query;
        var head = _admin.ChatHead;
        long after = long.TryParse(query["after"], out var a) && a >= 0 ? a : Math.Max(0, head - 100);
        var wait = int.TryParse(query["wait"], out var w) ? Math.Clamp(w, 0, MaxLongPollSeconds) : 0;
        var deadline = Environment.TickCount64 + wait * 1000L;
        IReadOnlyList<ChatEntry> entries = _admin.ChatSince(after, 200);
        while (entries.Count == 0 && Environment.TickCount64 < deadline && !context.RequestAborted.IsCancellationRequested)
        {
            try { await Task.Delay(150, context.RequestAborted).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            entries = _admin.ChatSince(after, 200);
        }
        var last = entries.Count > 0 ? entries[^1].Id : Math.Max(after, _admin.ChatHead);
        await Json(context.Response, new ChatPage(last, entries.ToArray()), WebUiJson.Default.ChatPage).ConfigureAwait(false);
    }

    private static (AdminCommand Command, string? Error) Build(string name, CommandRequest b)
    {
        string? text = Clean(b.Text);
        switch (name)
        {
            case "broadcast": return text is null ? (default, "text required") : (new(AdminCommandKind.Broadcast, Text: text), null);
            case "message":
                if (!Slot(b.Slot)) return (default, "slot 1..16 required");
                return text is null ? (default, "text required") : (new(AdminCommandKind.Message, b.Slot, Text: text), null);
            case "kick": return Slot(b.Slot) ? (new(AdminCommandKind.Kick, b.Slot, b.Value), null) : (default, "slot 1..16 required");
            case "ban":
                if (Slot(b.Slot)) return (new(AdminCommandKind.Ban, b.Slot, Text: Clean(b.Reason) ?? ""), null);
                if (b.Address is { Length: > 0 and <= 64 } address && IPAddress.TryParse(address, out var ip))
                    return (new(AdminCommandKind.BanAddress, Text: ip.ToString() + "|" + (Clean(b.Reason) ?? "")), null);
                return (default, "slot or a valid address required");
            case "unban":
                return b.Address is { Length: > 0 and <= 64 } lifted && IPAddress.TryParse(lifted, out var parsed)
                    ? (new(AdminCommandKind.Unban, Text: parsed.ToString()), null) : (default, "valid address required");
            case "lock": return (new(AdminCommandKind.Lock, Value: b.Value != 0 ? 1 : 0), null);
            case "pause": return (new(AdminCommandKind.Pause, Value: b.Value != 0 ? 1 : 0), null);
            case "max-players": return b.Value is >= 1 and <= 16 ? (new(AdminCommandKind.SetMaxPlayers, Value: b.Value), null) : (default, "value 1..16 required");
            case "password": return (new(AdminCommandKind.SetPassword, Text: b.Text is { Length: <= 32 } pw ? pw : null), b.Text is { Length: > 32 } ? "password too long" : null);
            case "name": return text is null ? (default, "text required") : (new(AdminCommandKind.SetName, Text: text), null);
            case "mode":
                return b.Value is >= 0 and <= 4 && b.Slot is >= 0 and <= 5 ? (new(AdminCommandKind.SetMode, b.Slot, b.Value), null) : (default, "value 0..4 and slot (level) 0..5 required");
            case "end-session": return (new(AdminCommandKind.EndSession), null);
            case "shutdown": return (new(AdminCommandKind.Shutdown), null);
        }
        return (default, "unknown command");
    }

    private static bool Slot(int slot) => slot is >= 1 and <= 16;

    /// <summary>Trims, drops control characters and limits the length; null when nothing is left.</summary>
    private static string? Clean(string? text)
    {
        if (text is null) return null;
        var builder = new StringBuilder(Math.Min(text.Length, MaxText));
        foreach (var c in text)
        {
            if (builder.Length >= MaxText) break;
            if (!char.IsControl(c)) builder.Append(c);
        }
        var result = builder.ToString().Trim();
        return result.Length == 0 ? null : result;
    }

    private static async Task Json<T>(HttpResponse response, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info)
    {
        response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(response.Body, value, info, response.HttpContext.RequestAborted).ConfigureAwait(false);
    }

    private static async Task Fail(HttpResponse response, int status, string message)
    {
        response.StatusCode = status;
        await Json(response, new CommandResult(false, message), WebUiJson.Default.CommandResult).ConfigureAwait(false);
    }
}
