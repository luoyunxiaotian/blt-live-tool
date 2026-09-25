using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace BiLi_live_Tool.Services.SystemMedia;

public sealed record NeteaseTrackInfo(
    string Title,
    string Artist,
    string Album,
    string CoverUrl,
    double DurationSec,
    long PlaytimeMs,
    string SongId = ""
);

/// <summary>
/// 读取网易云音乐 PC 客户端本地 SQLite 数据库（webdb.dat）获取实时歌曲信息、时长与起播时间戳。
/// 基于 Windows 10/11 内置 winsqlite3.dll，无需引入任何第三方库。
/// </summary>
public static class NeteaseWebDbReader
{
    private const int SQLITE_OK = 0;
    private const int SQLITE_ROW = 100;
    private const int SQLITE_OPEN_READONLY = 0x00000001;

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_open_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(string filename, out IntPtr db, int flags, string? zVfs);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_prepare_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(IntPtr db, string zSql, int nByte, out IntPtr ppStmt, IntPtr pzTail);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_step", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr pStmt);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_text", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_text(IntPtr pStmt, int iCol);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_column_int64", CallingConvention = CallingConvention.Cdecl)]
    private static extern long sqlite3_column_int64(IntPtr pStmt, int iCol);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_finalize", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr pStmt);

    [DllImport("winsqlite3.dll", EntryPoint = "sqlite3_close", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr db);

    private static readonly string DbPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NetEase", "CloudMusic", "Library", "webdb.dat");

    public static NeteaseTrackInfo? TryGetLatestTrack()
    {
        if (!File.Exists(DbPath)) return null;

        IntPtr db = IntPtr.Zero;
        IntPtr stmt = IntPtr.Zero;

        try
        {
            if (sqlite3_open_v2(DbPath, out db, SQLITE_OPEN_READONLY, null) != SQLITE_OK || db == IntPtr.Zero)
            {
                return null;
            }

            const string sql = "SELECT playtime, jsonStr FROM historyTracks ORDER BY playtime DESC LIMIT 1;";
            if (sqlite3_prepare_v2(db, sql, -1, out stmt, IntPtr.Zero) != SQLITE_OK || stmt == IntPtr.Zero)
            {
                return null;
            }

            if (sqlite3_step(stmt) == SQLITE_ROW)
            {
                long playtime = sqlite3_column_int64(stmt, 0);
                IntPtr textPtr = sqlite3_column_text(stmt, 1);
                string? json = Marshal.PtrToStringUTF8(textPtr);

                if (!string.IsNullOrEmpty(json))
                {
                    var node = JsonNode.Parse(json);
                    if (node != null)
                    {
                        string name = node["name"]?.GetValue<string>() ?? node["title"]?.GetValue<string>() ?? "";
                        string songId = "";
                        if (node["id"] is JsonValue idVal)
                        {
                            if (idVal.TryGetValue<long>(out var lid) && lid > 0)
                                songId = lid.ToString();
                            else if (idVal.TryGetValue<string>(out var sid))
                                songId = sid ?? "";
                        }

                        var artistsArr = node["artists"] as JsonArray;
                        string artists = "";
                        if (artistsArr != null && artistsArr.Count > 0)
                        {
                            artists = string.Join("/", artistsArr
                                .Select(a => a?["name"]?.GetValue<string>())
                                .Where(n => !string.IsNullOrEmpty(n)));
                        }

                        string album = node["album"]?["name"]?.GetValue<string>() ?? "";
                        string picUrl = node["album"]?["picUrl"]?.GetValue<string>() ?? "";
                        long durMs = node["duration"]?.GetValue<long>() ?? 0;
                        double durSec = durMs > 0 ? (durMs / 1000.0) : 0;

                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            return new NeteaseTrackInfo(name, artists, album, picUrl, durSec, playtime, songId);
                        }
                    }
                }
            }
        }
        catch
        {
            // 忽略文件竞争或读取异常
        }
        finally
        {
            if (stmt != IntPtr.Zero)
            {
                sqlite3_finalize(stmt);
            }
            if (db != IntPtr.Zero)
            {
                sqlite3_close(db);
            }
        }

        return null;
    }
}
