using System.Collections.Immutable;

namespace Solisten.Core.Model;

public enum RepeatMode { Off, One }

public enum EndBehavior { Stop, ContinueNextGroup }

public enum ZoomLevel { Track, Group }

public enum Layer { Listen, Navigate, Info }

public sealed record PlayingState(
    TrackId Track,
    PlaybackContext ContextSnapshot,
    int IndexInContext,
    TimeSpan Position,
    bool IsPaused,
    RepeatMode Repeat,
    TimeSpan Listened = default,
    bool PlayCounted = false);

/// <summary>BrowseLens 是观察位置，不是播放状态。</summary>
public sealed record FocusState(
    TrackId Track,
    int Index,
    PlaybackContext BrowseContext,
    ZoomLevel Zoom,
    string? Query,
    PlaybackContext? ContextBeforeSearch = null);

public sealed record ListeningViewport(
    TimeSpan Center,
    TimeSpan VisibleBefore,
    TimeSpan VisibleAfter,
    int DetailLevel)
{
    public static ListeningViewport Default(TimeSpan halfWidth) => new(TimeSpan.Zero, halfWidth, halfWidth, 0);
}

public sealed record CanvasState(
    TrackId? PlayingAnchor,
    TrackId? BrowseLens,
    ListeningViewport Viewport,
    bool ControlsRevealed);

public sealed record Settings(
    EndBehavior EndBehavior = EndBehavior.Stop,
    TimeSpan ListeningWindowHalfWidth = default)
{
    public static readonly Settings Default = new(EndBehavior.Stop, TimeSpan.FromSeconds(10));
}

public sealed record AppState(
    PlayingState? Playing,
    FocusState? Focus,
    CanvasState Canvas,
    Layer Layer,
    Arrangement Arrangement,
    LibraryIndex Library,
    Settings Settings,
    int ConsecutiveFailures = 0)
{
    public static AppState Initial(Settings? settings = null)
    {
        var s = settings ?? Settings.Default;
        return new AppState(
            Playing: null,
            Focus: null,
            Canvas: new CanvasState(null, null, ListeningViewport.Default(s.ListeningWindowHalfWidth), false),
            Layer: Layer.Listen,
            Arrangement: Arrangement.Folder,
            Library: LibraryIndex.Empty,
            Settings: s);
    }

    public Track? PlayingTrack => Playing is null ? null : Library.Find(Playing.Track);
}

/// <summary>恢复会话所需的最小信息（存入 session 表）。</summary>
public sealed record SessionSnapshot(
    TrackId Track,
    PlaybackContext ContextSnapshot,
    int IndexInContext,
    TimeSpan Position,
    RepeatMode Repeat,
    Arrangement Arrangement);

public static class ImmutableArrayExtensions
{
    public static ImmutableArray<T> OrEmpty<T>(this ImmutableArray<T> a) => a.IsDefault ? ImmutableArray<T>.Empty : a;
}
