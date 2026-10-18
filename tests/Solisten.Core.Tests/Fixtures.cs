using System.Collections.Immutable;
using Solisten.Core;
using Solisten.Core.Model;
using Solisten.Core.Sorting;

namespace Solisten.Core.Tests;

internal static class Fixtures
{
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(200);

    public static Track MakeTrack(long id, string folder, string file, string album = "", int disc = 1, int trackNo = 0,
        bool available = true, TimeSpan? duration = null, string artist = "Artist")
    {
        album = album.Length == 0 ? folder : album;
        var (albumKey, albumDisplay) = Arrangements.AlbumKey("", artist, album);
        return new Track(
            new TrackId(id),
            $@"C:\Music\{folder}\{file}",
            new TrackMeta(Path.GetFileNameWithoutExtension(file), artist, "", album, disc, trackNo, null, duration ?? Duration),
            new AudioFormat("flac", 44100, 16, 2),
            new GroupKeys(folder, folder, albumKey, albumDisplay),
            available);
    }

    /// <summary>三个文件夹：A(1,2,3) B(4,5) C(6,7)。</summary>
    public static LibraryIndex Library(params long[] unavailable) => LibraryIndex.From(
    [
        MakeTrack(1, "A", "01 a.flac", trackNo: 1, available: !unavailable.Contains(1)),
        MakeTrack(2, "A", "02 b.flac", trackNo: 2, available: !unavailable.Contains(2)),
        MakeTrack(3, "A", "10 c.flac", trackNo: 10, available: !unavailable.Contains(3)),
        MakeTrack(4, "B", "1.flac", trackNo: 1, available: !unavailable.Contains(4)),
        MakeTrack(5, "B", "2.flac", trackNo: 2, available: !unavailable.Contains(5)),
        MakeTrack(6, "C", "x.flac", trackNo: 1, available: !unavailable.Contains(6)),
        MakeTrack(7, "C", "y.flac", trackNo: 2, available: !unavailable.Contains(7)),
    ]);

    public static TrackId T(long id) => new(id);

    /// <summary>测试用 Store 包装：记录所有副作用。</summary>
    public sealed class Harness
    {
        public AppState State;
        public readonly List<Effect> Effects = [];

        public Harness(LibraryIndex? library = null, SessionSnapshot? session = null, Settings? settings = null)
        {
            State = AppState.Initial(settings);
            Do(new LibraryLoaded(library ?? Library(), session));
        }

        public ImmutableArray<Effect> Do(AppAction action)
        {
            var r = Reducer.Reduce(State, action);
            State = r.State;
            Effects.AddRange(r.Effects);
            return r.Effects;
        }

        public TrackId? Playing => State.Playing?.Track;

        /// <summary>导航到指定曲目并确认播放。</summary>
        public void PlayById(long id)
        {
            Do(new EnterNavigate());
            var ctx = State.Focus!.BrowseContext;
            int target = ctx.IndexOf(T(id));
            Do(new MoveFocus(target - State.Focus.Index));
            Do(new ConfirmFocus());
        }
    }
}
