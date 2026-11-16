using System.Net;
using System.Runtime.InteropServices;
using Sacred.GameServer;
using Sacred.GameServer.Config;
using Sacred.GameServer.Lan;
using Sacred.GameServer.Lobby;
using Sacred.GameServer.Session;
using Sacred.Network.Admission;

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine("""
        SacredGameServer - headless drop-in replacement for Sacred's GameServer.exe

        Reads Gameserver.cfg (NETWORK_PORT_TCP, NETWORK_PORT_LISTEN, NETWORK_PASSWORD, NETWORK_PRECONFIG_MODE,
        NETWORK_PRECONFIG_LEVEL, NETWORK_PRECONFIG_MAXPLAYERS, LADDER_EXPORT). Options use the same keys:
          --network-port-tcp=2005 --network-port-listen=2006 --network-password=secret
          --game-dir=/srv/sacred    directory with Sacred.exe, tincat2.dll and the Pak/Bin data (required unless --accept-any-data)
          --accept-any-data         do not verify client version/data fingerprints (development only)
          --network-lobby=lobby.example.org --network-lobbyport=7066   register the game in an Open Internet lobby
          --lobby-channel=5         lobby channel of the game (GameServer.exe uses 5)
          --server-name="My realm"  --public-address=203.0.113.5  --lan-announce=0  --tick-ms=20  --config=path
        """);
    return 0;
}

var config = GameServerConfig.Load(args);
Log.Open("Gameserver.log");
GoldLanCompatibility? compatibility = null;
if (!config.AcceptAnyData)
{
    if (config.GameDirectory is null) { Log.Info("ERROR: --game-dir is required (or pass --accept-any-data)."); return 2; }
    try { compatibility = GoldLanCompatibility.Read(config.GameDirectory); }
    catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
    { Log.Info($"ERROR: cannot read game data from '{config.GameDirectory}': {e.Message}"); return 2; }
}
var rules = new AdmissionRules(compatibility);
var advertised = config.PublicAddress is { } text && IPAddress.TryParse(text, out var parsed) ? parsed : IPAddress.Loopback;
var lan = config.LanAnnounce ? new LanAnnouncer(config, compatibility?.Version ?? 0, advertised) : null;
GameServerHost host;
try { host = new GameServerHost(config, rules, lan); }
catch (System.Net.Sockets.SocketException e) { Log.Info($"ERROR: The TCP port seems to be already in use! ({e.SocketErrorCode})"); return 3; }

LobbyClient? lobby = null;
if (config.LobbyHost is { } lobbyHost)
{
    var publicIp = advertised.Equals(IPAddress.Loopback) ? 0u : System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(advertised.MapToIPv4().GetAddressBytes());
    var record = host.LobbyRecord(compatibility?.Version ?? 0, publicIp);
    lobby = new LobbyClient(lobbyHost, config.LobbyPort > 0 ? config.LobbyPort : 7066, record);
    host.AttachLobby(lobby, record);
    lobby.Start();
}

using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, c => { c.Cancel = true; host.Stop(); });
using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, c => { c.Cancel = true; host.Stop(); });
host.Run();
lobby?.Dispose();
host.Dispose();
return 0;
