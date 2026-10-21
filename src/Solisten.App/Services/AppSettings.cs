using System.Text.Json;
using Solisten.Core.Model;
using Solisten.Rendering.Space;

namespace Solisten.App.Services;

public enum ReduceMotionMode { System, On, Off }

/// <summary>§8.7 设置项 + 窗口位置，保存在 %LOCALAPPDATA%\Solisten\settings.json。</summary>
public sealed record AppSettings
{
    public int? WindowX { get; init; }
    public int? WindowY { get; init; }
    public int? WindowWidth { get; init; }
    public int? WindowHeight { get; init; }
    public bool WindowMaximized { get; init; }

    /// <summary>音量滑块位置（0..1，对数刻度）。</summary>
    public double Volume { get; init; } = 0.85;

    public EndBehavior EndBehavior { get; init; } = EndBehavior.Stop;
    /// <summary>聆听空间：安静 / 标准（默认）/ 沉浸。</summary>
    public SpaceMode Space { get; init; } = SpaceMode.Standard;
    public ReduceMotionMode ReduceMotion { get; init; } = ReduceMotionMode.System;
    /// <summary>聆听画面显示歌词（有歌词且窗口够宽时）。</summary>
    public bool ShowLyrics { get; init; } = true;
    public string? OutputDeviceId { get; init; }

    /// <summary>用户已确认使用非官方的网易云接口适配器（默认关闭）。会话凭据不在这里，保存在 Windows 凭据管理器中。</summary>
    public bool NetEaseUnofficialAccepted { get; init; }

    /// <summary>除自建歌单外，用户勾选要同步的收藏歌单。</summary>
    public string[] NetEaseSelectedPlaylists { get; init; } = [];

    /// <summary>网易云在线播放音质；远端会按账号权限降级。</summary>
    public Solisten.Integrations.NetEase.Api.StreamQuality NetEaseQuality { get; init; } = Solisten.Integrations.NetEase.Api.StreamQuality.ExHigh;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static AppSettings Load(string path)
    {
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) is { } s) return s;
        }
        catch (Exception e) when (e is JsonException or IOException) { }
        return new AppSettings();
    }

    public void Save(string path)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, Options));
        File.Move(tmp, path, overwrite: true);
    }

    public Settings ToCore() => Settings.Default with
    {
        EndBehavior = EndBehavior,
    };
}
