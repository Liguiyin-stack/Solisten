using Solisten.Core;
using Solisten.Core.Model;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Solisten.App.Services;

/// <summary>
/// §8.6 SMTC：向系统报告当前歌曲、播放状态与时间线；响应媒体键、蓝牙耳机按键、锁屏和音量浮层上的控制。
/// 未打包的桌面应用通过一个禁用了命令管理器的 MediaPlayer 取得 SystemMediaTransportControls。
/// </summary>
public sealed class MediaControls : IDisposable
{
    private static readonly TimeSpan TimelineInterval = TimeSpan.FromSeconds(5);

    private readonly AppHost _host;
    private readonly MediaPlayer _player = new();
    private readonly SystemMediaTransportControls _smtc;
    private TrackId? _shownTrack;
    private DateTime _lastTimeline;

    public MediaControls(AppHost host)
    {
        _host = host;
        _player.CommandManager.IsEnabled = false;
        _smtc = _player.SystemMediaTransportControls;
        _smtc.IsPlayEnabled = true;
        _smtc.IsPauseEnabled = true;
        _smtc.IsNextEnabled = true;
        _smtc.IsPreviousEnabled = true;
        _smtc.IsStopEnabled = false;
        _smtc.ButtonPressed += OnButtonPressed;
        _smtc.PlaybackPositionChangeRequested += (_, e) => _host.Store.Dispatch(new Seek(e.RequestedPlaybackPosition));
        host.Store.StateChanged += (_, e) => Update(e.Previous, e.Current);
        Update(null, host.Store.State);
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs e)
    {
        AppAction? action = e.Button switch
        {
            SystemMediaTransportControlsButton.Play => new Resume(),
            SystemMediaTransportControlsButton.Pause => new Pause(),
            SystemMediaTransportControlsButton.Next => new Next(),
            SystemMediaTransportControlsButton.Previous => new Previous(),
            _ => null,
        };
        if (action is not null) _host.Store.Dispatch(action);
    }

    private void Update(AppState? prev, AppState s)
    {
        try
        {
            if (s.PlayingTrack is not { } track)
            {
                _smtc.IsEnabled = false;
                _shownTrack = null;
                return;
            }

            _smtc.IsEnabled = true;
            if (_shownTrack != track.Id) ShowTrack(track);

            var status = s.Playing!.IsPaused ? MediaPlaybackStatus.Paused : MediaPlaybackStatus.Playing;
            if (_smtc.PlaybackStatus != status) _smtc.PlaybackStatus = status;

            // 时间线：切歌、暂停/恢复、seek（位置跳变）时立即更新，播放中每 5 秒校准一次。
            var p = s.Playing;
            bool jumped = prev?.Playing is { } pp && pp.Track == p.Track && (p.Position - pp.Position).Duration() > TimeSpan.FromSeconds(2);
            bool stateChanged = prev?.Playing?.IsPaused != p.IsPaused || prev?.Playing?.Track != p.Track;
            if (jumped || stateChanged || DateTime.UtcNow - _lastTimeline > TimelineInterval)
            {
                _lastTimeline = DateTime.UtcNow;
                _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
                {
                    StartTime = TimeSpan.Zero,
                    MinSeekTime = TimeSpan.Zero,
                    Position = p.Position,
                    MaxSeekTime = track.Meta.Duration,
                    EndTime = track.Meta.Duration,
                });
            }
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            _host.Log.Write($"smtc update failed: {e.Message}");
        }
    }

    private void ShowTrack(Track track)
    {
        _shownTrack = track.Id;
        var u = _smtc.DisplayUpdater;
        u.ClearAll();
        u.Type = MediaPlaybackType.Music;
        u.MusicProperties.Title = track.Meta.Title;
        u.MusicProperties.Artist = track.Meta.Artist;
        u.MusicProperties.AlbumTitle = track.Meta.Album;
        u.MusicProperties.AlbumArtist = track.Meta.AlbumArtist;
        u.Update();

        var id = track.Id;
        _ = Task.Run(async () =>
        {
            try
            {
                if (_host.Library.Database.GetCoverHash(id) is not { } hash) return;
                var thumb = _host.Paths.Thumb(hash, 512);
                var path = File.Exists(thumb) ? thumb : _host.Paths.FindCover(hash);
                if (path is null) return;
                var file = await StorageFile.GetFileFromPathAsync(path);
                if (_shownTrack != id) return;
                u.Thumbnail = RandomAccessStreamReference.CreateFromFile(file);
                u.Update();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            {
                _host.Log.Write($"smtc thumbnail failed: {e.Message}");
            }
        });
    }

    public void Dispose()
    {
        _smtc.ButtonPressed -= OnButtonPressed;
        _player.Dispose();
    }
}
