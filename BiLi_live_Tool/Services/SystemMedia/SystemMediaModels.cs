using System;
using System.Text.Json.Serialization;

namespace BiLi_live_Tool.Services.SystemMedia;

/// <summary>
/// 表示当前操作系统或第三方播放器中正在播放的媒体状态与元数据
/// </summary>
public class SystemMediaTrack
{
    [JsonPropertyName("hasSong")]
    public bool HasSong { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "None"; // "Playing" | "Paused" | "None"

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("artist")]
    public string Artist { get; set; } = "";

    [JsonPropertyName("album")]
    public string Album { get; set; } = "";

    [JsonPropertyName("positionSec")]
    public double PositionSec { get; set; }

    [JsonPropertyName("durationSec")]
    public double DurationSec { get; set; }

    [JsonPropertyName("coverHash")]
    public string CoverHash { get; set; } = "";

    [JsonPropertyName("coverUrl")]
    public string CoverUrl { get; set; } = "";

    [JsonPropertyName("sourceApp")]
    public string SourceApp { get; set; } = "";

    [JsonPropertyName("requester")]
    public string Requester { get; set; } = "";

    [JsonPropertyName("platform")]
    public string Platform { get; set; } = "";

    [JsonPropertyName("songId")]
    public string SongId { get; set; } = "";

    [JsonPropertyName("volumePeak")]
    public float VolumePeak { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public override bool Equals(object? obj)
    {
        if (obj is not SystemMediaTrack other) return false;
        return HasSong == other.HasSong &&
               Status == other.Status &&
               Title == other.Title &&
               Artist == other.Artist &&
               Album == other.Album &&
               Platform == other.Platform &&
               SongId == other.SongId &&
               SourceApp == other.SourceApp &&
               Requester == other.Requester;
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(HasSong, Status, Title, Artist, Album, HashCode.Combine(Platform, SongId, SourceApp, Requester));
    }
}
