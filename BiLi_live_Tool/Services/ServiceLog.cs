using System.Collections.Generic;

namespace BiLi_live_Tool.Services;

/// <summary>
/// In-memory ring buffer of service-level log lines, surfaced to the UI through
/// /api/maui/server/log (the "\u670d\u52a1\u65e5\u5fd7" panel).
///
/// The Electron build showed the child Node server's stdout in that panel; here
/// everything runs in-process, so the services write the interesting lines
/// (connection, online rank, TTS, updates, tray actions) into this buffer.
/// </summary>
public static class ServiceLog
{
    public sealed record Line(string Time, string Level, string Src, string Msg);

    private const int Cap = 600;
    private static readonly object Gate = new();
    private static readonly LinkedList<Line> Buf = new();

    public static void Write(string level, string src, string msg)
    {
        lock (Gate)
        {
            Buf.AddLast(new Line(DateTime.Now.ToString("HH:mm:ss"), level, src, msg));
            while (Buf.Count > Cap) Buf.RemoveFirst();
        }
    }

    public static void Info(string src, string msg) => Write("info", src, msg);
    public static void Warn(string src, string msg) => Write("warn", src, msg);
    public static void Error(string src, string msg) => Write("error", src, msg);

    public static (List<Line> Lines, int Count) Snapshot(int max = 300)
    {
        lock (Gate)
        {
            var all = new List<Line>(Buf);
            var tail = all.Count > max ? all.GetRange(all.Count - max, max) : all;
            return (tail, Buf.Count);
        }
    }

    public static void Clear() { lock (Gate) Buf.Clear(); }
}
