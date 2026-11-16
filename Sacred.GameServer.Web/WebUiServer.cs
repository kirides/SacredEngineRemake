using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Sacred.GameServer.Web;

/// <summary>
/// The management UI on Kestrel with minimal API endpoints (<c>MapGet</c>/<c>MapPost</c>, no controllers):
/// <list type="bullet">
/// <item><c>GET /</c> server rendered page (<see cref="Pages"/>), forms post to <c>/actions/*</c> and redirect back,</item>
/// <item><c>GET /fragment</c> the server rendered state block the page swaps in every few seconds,</item>
/// <item><c>GET /api/chat/stream</c> chat as Server-Sent Events, <c>/api/*</c> JSON for scripts.</item>
/// </list>
/// Endpoints are plain <see cref="RequestDelegate"/>s (no reflection or model binding), JSON is source generated: trim and AOT clean.
/// </summary>
public sealed class WebUiServer : IAsyncDisposable
{
    private const int MaxText = 200;
    private readonly WebUiOptions _options;
    private readonly IServerAdmin _admin;
    private readonly Action<string> _log;
    private readonly byte[] _passwordHash;
    private WebApplication? _app;

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
        if (_options.PathBase.Length > 0) app.UsePathBase(_options.PathBase);
        app.Use(Gate);
        MapEndpoints(app);
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

    // ---- endpoints ----------------------------------------------------------------------------------------------------

    private void MapEndpoints(WebApplication app)
    {
        app.MapGet("/", (RequestDelegate)PageEndpoint);
        app.MapGet("/fragment", (RequestDelegate)FragmentEndpoint);
        app.MapGet("/api/overview", (RequestDelegate)(c => Json(c.Response, _admin.Overview(), WebUiJson.Default.ServerOverview)));
        app.MapGet("/api/players", (RequestDelegate)(c => Json(c.Response, _admin.Players().ToArray(), WebUiJson.Default.PlayerInfoArray)));
        app.MapGet("/api/bans", (RequestDelegate)(c => Json(c.Response, _admin.Bans().ToArray(), WebUiJson.Default.BanEntryArray)));
        app.MapGet("/api/chat", (RequestDelegate)ChatPageEndpoint);
        app.MapGet("/api/chat/stream", (RequestDelegate)ChatStreamEndpoint);
        app.MapPost("/api/{command}", (RequestDelegate)ApiCommandEndpoint);
        app.MapPost("/actions/{command}", (RequestDelegate)FormActionEndpoint);
    }

    private async Task PageEndpoint(HttpContext context)
    {
        var notice = context.Request.Query["notice"].ToString();
        var html = Pages.Index(_admin, string.IsNullOrEmpty(notice) ? null : notice);
        await Html(context.Response, html).ConfigureAwait(false);
    }

    private Task FragmentEndpoint(HttpContext context) => Html(context.Response, Pages.State(_admin));

    private async Task ChatPageEndpoint(HttpContext context)
    {
        var head = _admin.ChatHead;
        var after = long.TryParse(context.Request.Query["after"], out var a) && a >= 0 ? a : Math.Max(0, head - 100);
        var entries = _admin.ChatSince(after, 200);
        await Json(context.Response, new ChatPage(entries.Count > 0 ? entries[^1].Id : Math.Max(after, head), entries.ToArray()), WebUiJson.Default.ChatPage).ConfigureAwait(false);
    }

    /// <summary>Server-Sent Events: one <c>chat</c> event per line, <c>id</c> = chat id so EventSource resumes with Last-Event-ID.</summary>
    private async Task ChatStreamEndpoint(HttpContext context)
    {
        var response = context.Response;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers["X-Accel-Buffering"] = "no";
        var last = long.TryParse(context.Request.Headers["Last-Event-ID"], out var resume) ? resume
            : long.TryParse(context.Request.Query["after"], out var after) && after >= 0 ? after : Math.Max(0, _admin.ChatHead - 50);
        var cancel = context.RequestAborted;
        var idle = 0;
        try
        {
            await response.WriteAsync("retry: 3000\n\n", cancel).ConfigureAwait(false);
            await response.Body.FlushAsync(cancel).ConfigureAwait(false);
            while (!cancel.IsCancellationRequested)
            {
                var entries = _admin.ChatSince(last, 100);
                if (entries.Count == 0)
                {
                    await Task.Delay(200, cancel).ConfigureAwait(false);
                    if (++idle < 75) continue;               // ~15 s: keep proxies from closing an idle stream
                    idle = 0;
                    await response.WriteAsync(": keep-alive\n\n", cancel).ConfigureAwait(false);
                    await response.Body.FlushAsync(cancel).ConfigureAwait(false);
                    continue;
                }
                idle = 0;
                foreach (var entry in entries)
                {
                    var data = JsonSerializer.Serialize(entry, WebUiJson.Default.ChatEntry);
                    await response.WriteAsync($"id: {entry.Id}\nevent: chat\ndata: {data}\n\n", cancel).ConfigureAwait(false);
                    last = entry.Id;
                }
                await response.Body.FlushAsync(cancel).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task ApiCommandEndpoint(HttpContext context)
    {
        var response = context.Response;
        // Only JSON bodies: a cross-site form post cannot send this content type without a CORS preflight.
        if (context.Request.ContentType is null || !context.Request.ContentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        { response.StatusCode = StatusCodes.Status415UnsupportedMediaType; return; }
        CommandRequest body;
        try { body = await JsonSerializer.DeserializeAsync(context.Request.Body, WebUiJson.Default.CommandRequest, context.RequestAborted).ConfigureAwait(false) ?? new CommandRequest(); }
        catch (JsonException) { await Fail(response, 400, "invalid JSON").ConfigureAwait(false); return; }
        var (status, error) = Run((string)context.GetRouteValue("command")!, body);
        if (error is not null) await Fail(response, status, error).ConfigureAwait(false);
        else await Json(response, new CommandResult(true, null), WebUiJson.Default.CommandResult).ConfigureAwait(false);
    }

    /// <summary>The HTML forms: urlencoded body, answered with a redirect back to the page (Post/Redirect/Get).</summary>
    private async Task FormActionEndpoint(HttpContext context)
    {
        var request = context.Request; var response = context.Response;
        // Cookie-less Basic auth is attached by the browser to cross-site posts too: refuse anything a foreign page started.
        if (request.Headers["Sec-Fetch-Site"].ToString() == "cross-site") { response.StatusCode = StatusCodes.Status403Forbidden; return; }
        if (request.ContentType is null || !request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
        { response.StatusCode = StatusCodes.Status415UnsupportedMediaType; return; }
        var form = await request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
        var name = (string)context.GetRouteValue("command")!;
        var body = new CommandRequest(Int(form["slot"]), Int(form["value"]), form["text"], form["reason"], form["address"]);
        // The chat box posts "to" (0 = everybody) and the mode form "mode"/"level".
        if (name == "say") { name = Int(form["to"]) is var to and > 0 ? "message" : "broadcast"; body = body with { Slot = Int(form["to"]) }; }
        if (name == "mode") body = body with { Value = Int(form["mode"]), Slot = Int(form["level"]) };
        var (_, error) = Run(name, body);
        var notice = error ?? "Done.";
        var wantsJson = request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase);
        if (wantsJson) { response.StatusCode = error is null ? 200 : 400; await Json(response, new CommandResult(error is null, error), WebUiJson.Default.CommandResult).ConfigureAwait(false); return; }
        response.StatusCode = StatusCodes.Status303SeeOther;
        response.Headers.Location = $"{_options.PathBase}/?notice={Uri.EscapeDataString(notice)}";
    }

    private static int Int(string? value) => int.TryParse(value, out var n) ? n : 0;

    private (int Status, string? Error) Run(string name, CommandRequest body)
    {
        var (command, error) = Build(name, body);
        if (error is not null) return (name is "" ? 404 : 400, error);
        if (!_admin.Submit(command)) return (503, "command queue full");
        return (200, null);
    }

    // ---- gate: headers, host, CORS, auth --------------------------------------------------------------------------------

    private async Task Gate(HttpContext context, Func<Task> next)
    {
        var request = context.Request; var response = context.Response;
        SetSecurityHeaders(response);
        if (_options.PathBase.Length > 0 && !request.PathBase.HasValue) { response.StatusCode = StatusCodes.Status404NotFound; return; }
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
        await next().ConfigureAwait(false);
    }

    private void SetSecurityHeaders(HttpResponse response)
    {
        var headers = response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.CacheControl = "no-store";
        var ancestors = FrameAncestors();
        headers.ContentSecurityPolicy = $"default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; connect-src 'self'; img-src 'self' data:; frame-ancestors {ancestors}; base-uri 'none'; form-action 'self'";
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

    // ---- commands -----------------------------------------------------------------------------------------------------

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
                if (b.Address is { Length: > 0 and <= 64 } address && IPAddress.TryParse(address.Trim(), out var ip))
                    return (new(AdminCommandKind.BanAddress, Text: ip.ToString() + "|" + (Clean(b.Reason) ?? "")), null);
                return (default, "slot or a valid address required");
            case "unban":
                return b.Address is { Length: > 0 and <= 64 } lifted && IPAddress.TryParse(lifted.Trim(), out var parsed)
                    ? (new(AdminCommandKind.Unban, Text: parsed.ToString()), null) : (default, "valid address required");
            case "lock": return (new(AdminCommandKind.Lock, Value: b.Value != 0 ? 1 : 0), null);
            case "pause": return (new(AdminCommandKind.Pause, Value: b.Value != 0 ? 1 : 0), null);
            case "max-players": return b.Value is >= 1 and <= 16 ? (new(AdminCommandKind.SetMaxPlayers, Value: b.Value), null) : (default, "value 1..16 required");
            case "password": return b.Text is { Length: > 32 } ? (default, "password too long") : (new(AdminCommandKind.SetPassword, Text: b.Text), null);
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

    private static async Task Html(HttpResponse response, string html)
    {
        response.ContentType = "text/html; charset=utf-8";
        await response.WriteAsync(html, response.HttpContext.RequestAborted).ConfigureAwait(false);
    }

    private static async Task Fail(HttpResponse response, int status, string message)
    {
        response.StatusCode = status;
        await Json(response, new CommandResult(false, message), WebUiJson.Default.CommandResult).ConfigureAwait(false);
    }
}
