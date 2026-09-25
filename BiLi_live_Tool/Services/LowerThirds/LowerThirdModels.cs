using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BiLi_live_Tool.Services.LowerThirds;

public class LowerThirdSlot
{
    [JsonPropertyName("id")]
    public int Id { get; set; } // 1 ~ 10

    [JsonPropertyName("label")]
    public string Label { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("info")]
    public string Info { get; set; } = "";

    [JsonPropertyName("style")]
    public int Style { get; set; } = 1; // 1, 2, 3

    [JsonPropertyName("align")]
    public string Align { get; set; } = "left"; // left, center, right

    [JsonPropertyName("color1")]
    public string Color1 { get; set; } = "#1E40AF";

    [JsonPropertyName("color2")]
    public string Color2 { get; set; } = "#3B82F6";

    [JsonPropertyName("textColor1")]
    public string TextColor1 { get; set; } = "#FFFFFF";

    [JsonPropertyName("textColor2")]
    public string TextColor2 { get; set; } = "#E0E7FF";

    [JsonPropertyName("logo")]
    public string Logo { get; set; } = "";

    [JsonPropertyName("showLogo")]
    public bool ShowLogo { get; set; } = false;
}

public class LowerThirdChannel
{
    [JsonPropertyName("id")]
    public int Id { get; set; } // 1 ~ 4

    [JsonPropertyName("channelName")]
    public string ChannelName { get; set; } = ""; // e.g. "嘉宾名片", "通知横幅"

    [JsonPropertyName("active")]
    public bool Active { get; set; }

    [JsonPropertyName("currentSlotId")]
    public int CurrentSlotId { get; set; } = 1;

    // 当前活动的数据
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("info")]
    public string Info { get; set; } = "";

    [JsonPropertyName("style")]
    public int Style { get; set; } = 1;

    [JsonPropertyName("align")]
    public string Align { get; set; } = "left";

    [JsonPropertyName("color1")]
    public string Color1 { get; set; } = "#1E40AF";

    [JsonPropertyName("color2")]
    public string Color2 { get; set; } = "#3B82F6";

    [JsonPropertyName("textColor1")]
    public string TextColor1 { get; set; } = "#FFFFFF";

    [JsonPropertyName("textColor2")]
    public string TextColor2 { get; set; } = "#E0E7FF";

    [JsonPropertyName("fontFamily")]
    public string FontFamily { get; set; } = "Inter, PingFang SC, Microsoft YaHei";

    [JsonPropertyName("fontSize")]
    public int FontSize { get; set; } = 32;

    [JsonPropertyName("logo")]
    public string Logo { get; set; } = "";

    [JsonPropertyName("showLogo")]
    public bool ShowLogo { get; set; } = false;

    // 时间与触发控制
    [JsonPropertyName("animationTime")]
    public double AnimationTime { get; set; } = 1.0; // 出入场动画秒数

    [JsonPropertyName("activeTime")]
    public double ActiveTime { get; set; } = 6.0; // 停留展示秒数

    [JsonPropertyName("oneShot")]
    public bool OneShot { get; set; } = true; // 单次播放自动收起

    [JsonPropertyName("lockActive")]
    public bool LockActive { get; set; } = false; // 锁定常驻不自动退场

    // 10 个预设插槽
    [JsonPropertyName("slots")]
    public List<LowerThirdSlot> Slots { get; set; } = new();

    public static LowerThirdChannel CreateDefault(int id, string channelName, string color1, string color2)
    {
        var ch = new LowerThirdChannel
        {
            Id = id,
            ChannelName = channelName,
            Color1 = color1,
            Color2 = color2,
            Name = id == 1 ? "主持人 / 主播" : (id == 2 ? "特邀嘉宾" : $"通道 {id}"),
            Info = id == 1 ? "欢迎来到直播间" : "分享与交流",
            Slots = new List<LowerThirdSlot>()
        };

        for (int i = 1; i <= 10; i++)
        {
            ch.Slots.Add(new LowerThirdSlot
            {
                Id = i,
                Label = $"预设槽 {i}",
                Name = i == 1 ? ch.Name : $"嘉宾 {i}",
                Info = i == 1 ? ch.Info : "简介或社交账号",
                Style = ch.Style,
                Align = ch.Align,
                Color1 = ch.Color1,
                Color2 = ch.Color2,
                TextColor1 = "#FFFFFF",
                TextColor2 = "#E0E7FF"
            });
        }
        return ch;
    }
}

public class LowerThirdConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    [JsonPropertyName("autoMusicBubble")]
    public bool AutoMusicBubble { get; set; } = true; // 切歌时自动弹出音乐 Lower Third 气泡

    [JsonPropertyName("musicBubbleDuration")]
    public double MusicBubbleDuration { get; set; } = 6.0;

    [JsonPropertyName("avoidConflict")]
    public bool AvoidConflict { get; set; } = true; // 激活嘉宾名片时自动收起音乐挂件避让

    [JsonPropertyName("channels")]
    public List<LowerThirdChannel> Channels { get; set; } = new();
}
