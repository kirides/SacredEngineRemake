using System.Text;

namespace Sacred.GameServer;

/// <summary>Minimal logger: console plus Gameserver.log (original file name). Not used on per-packet paths.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _file;

    public static void Open(string path)
    {
        try { _file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true }; }
        catch (IOException) { _file = null; }
        catch (UnauthorizedAccessException) { _file = null; }
    }

    public static void Info(string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {message}";
        lock (Gate)
        {
            Console.Out.WriteLine(line);
            _file?.WriteLine(line);
        }
    }
}
