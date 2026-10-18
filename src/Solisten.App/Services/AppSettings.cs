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
    public string? OutputDeviceId { get; init; }

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
