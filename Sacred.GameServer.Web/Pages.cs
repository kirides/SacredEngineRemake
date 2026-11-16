using System.Net;
using System.Text;

namespace Sacred.GameServer.Web;

/// <summary>
/// Server side rendering of the management page. Plain string building with HTML encoding on every dynamic value (player names
/// and chat text come from the network). The page is rendered once with the chat history; a few lines of script then swap the
/// <c>#state</c> block (<c>/fragment</c>) and append chat events from the SSE stream.
/// </summary>
internal static class Pages
{
    private static readonly string[] Modes = ["Default", "Free game", "Player killer", "Campaign", "Underworld campaign"];
    private static readonly string[] Levels = ["Any level", "Bronze", "Silver", "Gold", "Platinum", "Niobium"];

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");

    public static string Index(IServerAdmin admin, string? notice)
    {
        var o = admin.Overview();
        var sb = new StringBuilder(16 * 1024);
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\"><title>")
          .Append(E(o.Name)).Append(" - Sacred GameServer</title><style>").Append(Style).Append("</style></head><body>");
        sb.Append("<div id=\"state\">").Append(State(admin)).Append("</div>");
        if (notice is not null) sb.Append("<div id=\"notice\">").Append(E(notice)).Append("</div>");
        sb.Append("<main><section><h2>Chat</h2><div id=\"chat\" aria-live=\"polite\">");
        foreach (var entry in admin.ChatSince(Math.Max(0, admin.ChatHead - 50), 50)) ChatLine(sb, entry);
        sb.Append("</div><form id=\"chatForm\" class=\"row\" method=\"post\" action=\"actions/say\"><select name=\"to\" id=\"chatTo\"><option value=\"0\">Everybody (broadcast)</option>");
        foreach (var p in admin.Players()) sb.Append("<option value=\"").Append(p.Slot).Append("\">#").Append(p.Slot).Append(' ').Append(E(p.Name)).Append("</option>");
        sb.Append("</select><input name=\"text\" id=\"chatText\" type=\"text\" maxlength=\"120\" placeholder=\"Message as the server\" autocomplete=\"off\" required><button type=\"submit\">Send</button></form></section>");
        sb.Append("<section><h2>Session</h2>");
        Form(sb, "lock", $"<input type=\"hidden\" name=\"value\" value=\"{(o.Locked ? 0 : 1)}\"><button>{(o.Locked ? "Unlock session" : "Lock session")}</button>");
        Form(sb, "pause", $"<input type=\"hidden\" name=\"value\" value=\"{(o.Paused ? 0 : 1)}\"><button>{(o.Paused ? "Continue game" : "Pause game")}</button>");
        Form(sb, "max-players", $"<label>Max players <input name=\"value\" type=\"number\" min=\"1\" max=\"16\" value=\"{o.MaxPlayers}\" style=\"width:5rem\"></label><button>Set</button>");
        Form(sb, "name", $"<label>Name <input name=\"text\" type=\"text\" maxlength=\"60\" value=\"{E(o.Name)}\"></label><button>Set</button>");
        Form(sb, "password", "<label>Password <input name=\"text\" type=\"password\" maxlength=\"32\" placeholder=\"empty removes it\" autocomplete=\"new-password\"></label><button>Set</button>");
        var mode = new StringBuilder("<select name=\"mode\">");
        for (var i = 0; i < Modes.Length; i++) mode.Append($"<option value=\"{i}\"{(i == o.Mode ? " selected" : "")}>{Modes[i]}</option>");
        mode.Append("</select><select name=\"level\">");
        for (var i = 0; i < Levels.Length; i++) mode.Append($"<option value=\"{i}\"{(i == o.Level ? " selected" : "")}>{Levels[i]}</option>");
        mode.Append("</select><button>Apply</button><span class=\"dim\">only before the host world exists</span>");
        Form(sb, "mode", mode.ToString());
        Form(sb, "end-session", "<button class=\"danger\">End session (kick everybody)</button>", "Kick every player and reset the session?");
        Form(sb, "shutdown", "<button class=\"danger\">Shut the server down</button>", "Shut the whole server down? It will not restart by itself.");
        sb.Append("</section><section><h2>Ban an address</h2>");
        Form(sb, "ban", "<input name=\"address\" type=\"text\" placeholder=\"IP address\" required><input name=\"reason\" type=\"text\" placeholder=\"Reason\" maxlength=\"100\"><button class=\"danger\">Ban</button>");
        sb.Append("</section></main><script>").Append(Script).Append("</script></body></html>");
        return sb.ToString();
    }

    /// <summary>Header with the overview, player table and ban list: everything that changes while the page stays open.</summary>
    public static string State(IServerAdmin admin)
    {
        var o = admin.Overview();
        var sb = new StringBuilder(4 * 1024);
        sb.Append("<header><h1>").Append(E(o.Name)).Append("</h1>")
          .Append("<div class=\"stat\"><span>players</span><b>").Append(o.Players).Append(" / ").Append(o.MaxPlayers).Append("</b></div>")
          .Append("<div class=\"stat\"><span>uptime</span><b>").Append(Duration(o.UptimeSeconds)).Append("</b></div>")
          .Append("<div class=\"stat\"><span>tick</span><b>").Append(o.Tick).Append("</b></div>")
          .Append("<div class=\"stat\"><span>mode</span><b>").Append(E(o.Mode >= 0 && o.Mode < Modes.Length ? Modes[o.Mode] : o.Mode.ToString())).Append("</b></div>");
        Pill(sb, o.Locked ? "locked" : "open", o.Locked); Pill(sb, o.Paused ? "paused" : "running", !o.Paused); Pill(sb, o.PasswordSet ? "password" : "no password", o.PasswordSet);
        Pill(sb, o.WorldAvailable ? "world up" : "lobby", o.WorldAvailable);
        Pill(sb, !o.LobbyConfigured ? "no lobby" : o.LobbyOnline ? "lobby online" : "lobby offline", o.LobbyOnline);
        sb.Append("</header><main><section><h2>Players</h2>");
        var players = admin.Players();
        if (players.Count == 0) sb.Append("<p class=\"dim\">Nobody is connected.</p>");
        else
        {
            sb.Append("<table><thead><tr><th>#</th><th>Name</th><th>Address</th><th>Ping</th><th>State</th><th></th></tr></thead><tbody>");
            foreach (var p in players)
            {
                sb.Append("<tr><td>").Append(p.Slot).Append("</td><td class=\"name\">").Append(E(p.Name)).Append(p.IsHost ? " (host)" : "").Append("</td><td>")
                  .Append(E(p.Address)).Append(' ').Append(E(p.Transport)).Append("</td><td>").Append(p.LatencyMs).Append(" ms</td><td>")
                  .Append(p.InGame ? "in game" : p.Ready ? "ready" : "lobby").Append("</td><td class=\"actions\">");
                Form(sb, "kick", $"<input type=\"hidden\" name=\"slot\" value=\"{p.Slot}\"><button>Kick</button>", $"Kick {p.Name}?", inline: true);
                Form(sb, "ban", $"<input type=\"hidden\" name=\"slot\" value=\"{p.Slot}\"><input name=\"reason\" placeholder=\"reason\" size=\"10\" maxlength=\"100\"><button class=\"danger\">Ban</button>", $"Ban the address of {p.Name}?", inline: true);
                sb.Append("</td></tr>");
            }
            sb.Append("</tbody></table>");
        }
        sb.Append("</section><section><h2>Bans</h2>");
        var bans = admin.Bans();
        if (bans.Count == 0) sb.Append("<p class=\"dim\">No bans.</p>");
        else
        {
            sb.Append("<table><thead><tr><th>Address</th><th>Reason</th><th>Since (UTC)</th><th></th></tr></thead><tbody>");
            foreach (var b in bans)
            {
                sb.Append("<tr><td>").Append(E(b.Address)).Append("</td><td>").Append(E(b.Reason)).Append("</td><td>")
                  .Append(DateTimeOffset.FromUnixTimeMilliseconds(b.CreatedUnixMs).ToString("yyyy-MM-dd HH:mm")).Append("</td><td>");
                Form(sb, "unban", $"<input type=\"hidden\" name=\"address\" value=\"{E(b.Address)}\"><button>Lift</button>", null, inline: true);
                sb.Append("</td></tr>");
            }
            sb.Append("</tbody></table>");
        }
        sb.Append("</section></main>");
        return sb.ToString();
    }

    private static void Form(StringBuilder sb, string action, string inner, string? confirm = null, bool inline = false)
    {
        sb.Append("<form method=\"post\" action=\"actions/").Append(action).Append("\" class=\"").Append(inline ? "inline" : "row").Append('"');
        if (confirm is not null) sb.Append(" data-confirm=\"").Append(E(confirm)).Append('"');
        sb.Append('>').Append(inner).Append("</form>");
    }

    private static void Pill(StringBuilder sb, string text, bool on) =>
        sb.Append("<span class=\"pill").Append(on ? " on" : "").Append("\">").Append(E(text)).Append("</span>");

    private static string Duration(double seconds)
    {
        var s = (long)seconds;
        var d = s / 86400; var h = s % 86400 / 3600; var m = s % 3600 / 60;
        return (d > 0 ? d + "d " : "") + (d > 0 || h > 0 ? h + "h " : "") + m + "m";
    }

    /// <summary>One chat line, the same markup the script builds for live events.</summary>
    private static void ChatLine(StringBuilder sb, ChatEntry e)
    {
        var who = e.Kind switch
        {
            ChatKind.Public => e.FromName + ": ",
            ChatKind.Private => $"{e.FromName} (#{e.FromSlot}) to #{e.ToSlot}: ",
            ChatKind.Broadcast => "SERVER: ",
            ChatKind.ServerMessage => $"SERVER to #{e.ToSlot}: ",
            _ => "* " + (e.FromName.Length > 0 ? e.FromName + " " : ""),
        };
        sb.Append("<div class=\"k").Append((int)e.Kind).Append("\" data-id=\"").Append(e.Id).Append("\"><span class=\"t\">")
          .Append(DateTimeOffset.FromUnixTimeMilliseconds(e.TimeUnixMs).ToString("HH:mm:ss")).Append(" </span>").Append(E(who + e.Text)).Append("</div>");
    }

    private const string Style = """
:root{color-scheme:dark;--bg:#14110e;--panel:#201b16;--line:#3a3128;--text:#e9dfcf;--dim:#9b8f7d;--gold:#d9a441;--red:#c7503f;--green:#6fb06a}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 system-ui,sans-serif}
header{display:flex;flex-wrap:wrap;gap:.5rem 1.5rem;align-items:baseline;padding:.8rem 1rem;border-bottom:1px solid var(--line);background:var(--panel)}
header h1{margin:0;font-size:1.15rem;color:var(--gold)}.stat span{color:var(--dim)}.stat b{margin-left:.3rem}
main{display:grid;gap:1rem;padding:1rem;grid-template-columns:repeat(auto-fit,minmax(420px,1fr))}
section{background:var(--panel);border:1px solid var(--line);border-radius:6px;padding:.8rem 1rem;min-width:0}
section h2{margin:0 0 .6rem;font-size:.95rem;color:var(--gold);text-transform:uppercase;letter-spacing:.05em}
table{width:100%;border-collapse:collapse}th,td{text-align:left;padding:.25rem .4rem;border-bottom:1px solid var(--line);white-space:nowrap}th{color:var(--dim);font-weight:normal}
td.name{max-width:14rem;overflow:hidden;text-overflow:ellipsis}td.actions{display:flex;gap:.3rem}
button,input,select{font:inherit;color:var(--text);background:#2c251e;border:1px solid var(--line);border-radius:4px;padding:.25rem .6rem}
button{cursor:pointer}button:hover{border-color:var(--gold)}button.danger{border-color:var(--red);color:#f0b3a8}button.danger:hover{background:#4a231d}
.row{display:flex;gap:.4rem;flex-wrap:wrap;align-items:center;margin:.35rem 0}.inline{display:inline-flex;gap:.2rem}.row>input[type=text],.row>input[type=password]{flex:1 1 10rem}
#chat{height:22rem;overflow-y:auto;background:#17130f;border:1px solid var(--line);border-radius:4px;padding:.4rem .6rem;font:13px ui-monospace,monospace}
#chat div{white-space:pre-wrap;word-break:break-word}.t{color:var(--dim)}.k1{color:#c9a6e8}.k2{color:var(--gold)}.k3{color:#e8a6c9}.k4{color:var(--dim);font-style:italic}
.pill{padding:0 .4rem;border-radius:3px;background:#3a3128;font-size:12px;margin-left:.3rem}.pill.on{background:#2f4a2c;color:var(--green)}
#notice{position:fixed;right:1rem;bottom:1rem;padding:.5rem .9rem;border-radius:4px;background:#2f4a2c;max-width:30rem}.dim{color:var(--dim)}
""";

    // Confirm dialogs on forms, the state swap, the chat stream. No frameworks; all text goes in through textContent.
    private const string Script = """
(function(){
'use strict';
document.addEventListener('submit',function(e){var c=e.target.dataset&&e.target.dataset.confirm;if(c&&!confirm(c))e.preventDefault();});
var chat=document.getElementById('chat');chat.scrollTop=chat.scrollHeight;
var n=document.getElementById('notice');if(n){setTimeout(function(){n.remove();},3500);history.replaceState(null,'',location.pathname);}
var kinds=function(e){switch(e.kind){case 0:return e.fromName+': ';case 1:return e.fromName+' (#'+e.fromSlot+') to #'+e.toSlot+': ';case 2:return 'SERVER: ';case 3:return 'SERVER to #'+e.toSlot+': ';default:return '* '+(e.fromName?e.fromName+' ':'');}};
function addChat(e){var stick=chat.scrollTop+chat.clientHeight>=chat.scrollHeight-30;var d=document.createElement('div');d.className='k'+e.kind;d.dataset.id=e.id;
var t=document.createElement('span');t.className='t';t.textContent=new Date(e.timeUnixMs).toLocaleTimeString()+' ';d.append(t,kinds(e)+e.text);chat.append(d);
while(chat.childElementCount>500)chat.firstElementChild.remove();if(stick)chat.scrollTop=chat.scrollHeight;}
var last=chat.lastElementChild?chat.lastElementChild.dataset.id:'0';
var es=new EventSource('api/chat/stream?after='+last);es.addEventListener('chat',function(m){addChat(JSON.parse(m.data));});
document.getElementById('chatForm').addEventListener('submit',function(e){e.preventDefault();var f=e.target;
fetch(f.action,{method:'POST',headers:{'Accept':'application/json'},body:new URLSearchParams(new FormData(f))}).then(function(r){if(r.ok)f.text.value='';});});
setInterval(function(){fetch('fragment',{cache:'no-store'}).then(function(r){return r.ok?r.text():null;}).then(function(h){var s=document.getElementById('state');if(h&&!s.contains(document.activeElement))s.innerHTML=h;});},3000);
})();
""";
}
