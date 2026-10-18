using Solisten.Core.Model;
using Solisten.Core.Sorting;
using static Solisten.Core.Tests.Fixtures;

namespace Solisten.Core.Tests;

public class SortingTests
{
    [Fact]
    public void NaturalSort_ComparesDigitRunsNumerically()
    {
        string[] input = ["10 c", "2 b", "01 a", "1 a", "Track 9", "track 10", "B", "a"];
        var sorted = input.OrderBy(x => x, NaturalComparer.Instance).ToArray();
        Assert.Equal(["1 a", "01 a", "2 b", "10 c", "a", "B", "Track 9", "track 10"], sorted);
    }

    [Fact]
    public void FolderArrangement_GroupsByFolderAndSortsFileNamesNaturally()
    {
        var ctx = Library().Arrange(Arrangement.Folder);
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], ctx.Order.Select(t => t.Value));
        Assert.Equal(["A", "B", "C"], ctx.Groups.Select(g => g.Key));
        Assert.Equal([0, 3, 5], ctx.Groups.Select(g => g.Start));
    }

    [Fact]
    public void AlbumArrangement_SortsByDiscThenTrackThenFileName()
    {
        var lib = LibraryIndex.From(
        [
            MakeTrack(1, "X", "z.flac", album: "Alb", disc: 2, trackNo: 1),
            MakeTrack(2, "X", "b.flac", album: "Alb", disc: 1, trackNo: 2),
            MakeTrack(3, "Y", "a.flac", album: "Alb", disc: 1, trackNo: 2),
            MakeTrack(4, "Y", "q.flac", album: "Alb", disc: 1, trackNo: 1),
        ]);
        var ctx = lib.Arrange(Arrangement.Album);
        Assert.Single(ctx.Groups);
        Assert.Equal([4, 3, 2, 1], ctx.Order.Select(t => t.Value));
    }

    [Fact]
    public void AlbumKey_PrefersAlbumArtist()
    {
        Assert.Equal(Arrangements.AlbumKey("VA", "Someone", "Hits").Key, Arrangements.AlbumKey("va", "Other", "hits").Key);
        Assert.NotEqual(Arrangements.AlbumKey("", "A", "Hits").Key, Arrangements.AlbumKey("", "B", "Hits").Key);
    }

    [Fact]
    public void ListeningArrangement_HasRecentFavoritesUnplayedSegments()
    {
        var now = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        var lib = LibraryIndex.From(Library().Tracks.Values,
        [
            new(T(5), new UserTrackData(2, now.AddHours(-1), false)),
            new(T(3), new UserTrackData(1, now, true)),
        ]);
        var ctx = lib.Arrange(Arrangement.Listening);
        Assert.Equal([Arrangements.RecentKey, Arrangements.FavoritesKey, Arrangements.UnplayedKey], ctx.Groups.Select(g => g.Key));
        Assert.Equal([3, 5, 3, 1, 2, 4, 6, 7], ctx.Order.Select(t => t.Value));
    }
}

public class PaletteTests
{
    [Fact]
    public void AccentOnBase_FallsBackToTextWhenAccentBlendsIntoBackground()
    {
        var blue = new Rgb(0x50, 0x70, 0xA0);
        var p = new Palette(blue, new Rgb(0x58, 0x78, 0xA8), new Rgb(0x10, 0x18, 0x28), new Rgb(255, 255, 255));
        Assert.Equal(p.TextOnBase, p.AccentOnBase);

        var vivid = p with { Accent = new Rgb(0xFF, 0xD0, 0x60) };
        Assert.Equal(vivid.Accent, vivid.AccentOnBase);
    }

    [Fact]
    public void Contrast_MatchesWcagReferenceValues()
    {
        Assert.Equal(21.0, Rgb.Contrast(new Rgb(0, 0, 0), new Rgb(255, 255, 255)), 2);
        Assert.Equal(1.0, Rgb.Contrast(new Rgb(9, 99, 199), new Rgb(9, 99, 199)), 6);
    }
}
