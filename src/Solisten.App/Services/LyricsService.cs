using System.Collections.Concurrent;
using Solisten.App.Integrations;
using Solisten.Core.Model;
using Solisten.Library.LyricSources;

namespace Solisten.App.Services;

/// <summary>
/// 为一首歌找歌词，按顺序：
/// <list type="number">
/// <item>同名 .lrc 文件（用户自己准备的，优先级最高）；</item>
/// <item>音频文件内嵌的、带时间轴的歌词；</item>
/// <item>网易云歌词（在线歌曲，或匹配到网易云的本地歌曲；先读缓存，已登录时才联网）；</item>
/// <item>音频文件内嵌的、没有时间轴的歌词。</item>
/// </list>
/// 结果在内存中按曲目缓存；网络失败的结果不缓存，下次切到这首歌时再试。
/// </summary>
public sealed class LyricsService(NetEaseIntegrationViewModel? netease)
{
    private readonly ConcurrentDictionary<TrackId, Lyrics?> _memory = new();

    public async Task<Lyrics?> LoadAsync(Track track, CancellationToken ct)
    {
        if (_memory.TryGetValue(track.Id, out var cached)) return cached;

        Lyrics? result;
        bool known = true;
        if (track.Id.IsRemote)
        {
            (known, result) = await FromNetEaseAsync(track.Id, ct).ConfigureAwait(false);
        }
        else
        {
            result = await Task.Run(() => LocalLyrics.FromSidecar(track.Path), ct).ConfigureAwait(false);
            if (result is null)
            {
                var embedded = await Task.Run(() => LocalLyrics.FromTags(track.Path), ct).ConfigureAwait(false);
                if (embedded is { IsSynced: true }) result = embedded;
                else
                {
                    var (k, online) = await FromNetEaseAsync(track.Id, ct).ConfigureAwait(false);
                    known = k || embedded is not null;
                    result = online ?? embedded;
                }
            }
        }
        if (known) _memory[track.Id] = result;
        return result;
    }

    /// <summary>本地文件变了（例如用户放进了 .lrc）：忘掉这首歌的结果。</summary>
    public void Forget(TrackId id) => _memory.TryRemove(id, out _);

    private async Task<(bool Known, Lyrics? Lyrics)> FromNetEaseAsync(TrackId id, CancellationToken ct)
    {
        if (netease is null) return (true, null);
        var remote = id.IsRemote ? netease.Online.RemoteOf(id) : netease.Database.FindRemoteFor(netease.Provider.Provider, id);
        if (remote is not { } r) return (true, null);
        var (known, lyrics) = await netease.Online.LyricsAsync(r, netease.IsConnected, ct).ConfigureAwait(false);
        return (known, lyrics is null ? null : LrcParser.Parse(lyrics.Lyric, LyricsSource.NetEase, lyrics.Translation));
    }
}
