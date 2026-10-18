using System.Collections.Immutable;
using Solisten.Core.Model;

namespace Solisten.Core;

/// <summary>Reducer 产生的副作用描述，由宿主执行。Core 本身不触碰音频、磁盘或界面。</summary>
public abstract record Effect;

/// <summary>在引擎中打开曲目并从 StartAt 开始；Paused 为 true 时只加载不出声（启动恢复）。</summary>
public sealed record LoadTrack(TrackId Track, TimeSpan StartAt, bool Paused) : Effect;
/// <summary>告诉引擎快照中的无缝后继（null 表示没有后继）。</summary>
public sealed record SetSuccessor(TrackId? Track) : Effect;
public sealed record PauseAudio : Effect;
public sealed record ResumeAudio : Effect;
public sealed record SeekAudio(TimeSpan Position) : Effect;
public sealed record StopAudio : Effect;

public sealed record PersistSession(SessionSnapshot Session) : Effect;
public sealed record RecordPlay(TrackId Track) : Effect;
public sealed record RunSearch(string Query) : Effect;
public sealed record PrioritizeAnalysis(TrackId Track) : Effect;
public sealed record SaveMarker(TrackId Track, TimeSpan Position, string? Label) : Effect;
public sealed record SaveMarkerLabel(TrackId Track, long MarkerId, string? Label) : Effect;
public sealed record RemoveMarker(TrackId Track, long MarkerId) : Effect;
public sealed record SaveTags(TrackId Track, ImmutableArray<string> Tags) : Effect;
public sealed record SaveNote(TrackId Track, string Note) : Effect;
public sealed record SaveFavorite(TrackId Track, bool Favorite) : Effect;
public sealed record ShowToast(string Message) : Effect;
