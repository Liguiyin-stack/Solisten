using System.Collections.Immutable;
using Solisten.Core.Model;

namespace Solisten.Core;

/// <summary>§2.4：所有状态修改的唯一入口。</summary>
public abstract record AppAction;

// —— 播放 ——
public sealed record PlayTrack(TrackId Track, PlaybackContext Context, TimeSpan StartAt = default) : AppAction;
public sealed record TogglePause : AppAction;
public sealed record Pause : AppAction;
public sealed record Resume : AppAction;
public sealed record Seek(TimeSpan Position) : AppAction;
public sealed record SeekBy(TimeSpan Delta) : AppAction;
public sealed record Next : AppAction;
public sealed record Previous : AppAction;
public sealed record ToggleRepeatOne : AppAction;
public sealed record ShuffleSnapshot(int Seed) : AppAction;

// —— 来自音频引擎 ——
/// <summary>引擎已经无缝衔接到快照中的下一首（同一帧内声音与界面一起切换）。</summary>
public sealed record TrackBoundaryReached(TrackId NewTrack) : AppAction;
/// <summary>引擎没有后继曲目，当前曲目已播完。</summary>
public sealed record TrackEnded(TrackId Track) : AppAction;
public sealed record PositionChanged(TimeSpan Position) : AppAction;
public sealed record PlaybackFailed(TrackId Track, string Reason) : AppAction;
public sealed record OutputDeviceLost : AppAction;

// —— 导航层 ——
public sealed record EnterNavigate : AppAction;
public sealed record MoveFocus(int Delta) : AppAction;
public sealed record MoveFocusToGroup(int Delta) : AppAction;
public sealed record ConfirmFocus : AppAction;
public sealed record CancelNavigate : AppAction;
public sealed record ReturnFocusToPlaying : AppAction;
public sealed record SetZoom(ZoomLevel Level) : AppAction;
public sealed record SetArrangement(Arrangement Arrangement) : AppAction;
public sealed record SetQuery(string Text) : AppAction;
public sealed record ClearQuery : AppAction;
/// <summary>搜索结果回到 Store（由宿主执行 RunSearch 后派发）。</summary>
public sealed record SearchResultsArrived(string Query, ImmutableArray<TrackId> Results) : AppAction;
/// <summary>外部打开文件：以该文件所在文件夹为临时 BrowseContext，不自动切歌。</summary>
public sealed record RevealTrack(TrackId Track) : AppAction;

// —— 层 ——
public sealed record EnterInfo : AppAction;
public sealed record ExitLayer : AppAction;

// —— 画布 ——
public sealed record RevealControls : AppAction;
public sealed record HideControls : AppAction;
public sealed record SetListeningViewport(ListeningViewport Viewport) : AppAction;

// —— 个人痕迹 ——
public sealed record AddMarker(TimeSpan Position, string? Label = null) : AppAction;
/// <summary>标记由 Solisten 的数据库编号（MarkerId），只影响个人痕迹，不影响播放状态。</summary>
public sealed record RenameMarker(TrackId Track, long MarkerId, string? Label) : AppAction;
public sealed record DeleteMarker(TrackId Track, long MarkerId) : AppAction;
public sealed record SetTags(TrackId Track, ImmutableArray<string> Tags) : AppAction;
public sealed record SetNote(TrackId Track, string Note) : AppAction;
public sealed record ToggleFavorite(TrackId Track) : AppAction;

// —— 曲库 / 设置 ——
public sealed record LibraryLoaded(LibraryIndex Library, SessionSnapshot? Session) : AppAction;
public sealed record LibraryUpdated(LibraryIndex Library) : AppAction;
public sealed record SetEndBehavior(EndBehavior Behavior) : AppAction;
public sealed record SetListeningWindow(TimeSpan HalfWidth) : AppAction;
