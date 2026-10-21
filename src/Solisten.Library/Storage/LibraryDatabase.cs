using System.Collections.Immutable;
using System.Text.Json;
using Dapper;
using Microsoft.Data.Sqlite;
using Solisten.Core.Model;
using Solisten.Core.Sorting;
using Solisten.Library.Search;

namespace Solisten.Library.Storage;

public sealed record LibraryRoot(long Id, string Path, DateTimeOffset AddedAt);

public sealed record FileState(long Id, long Size, long MTime, bool Available);

/// <summary>扫描后待写入的一首歌。</summary>
public sealed record ScannedTrack(string RelPath, long Size, long MTime, TrackMeta Meta, AudioFormat Format, string? CoverHash);

public sealed record Marker(long Id, TrackId Track, TimeSpan Position, string? Label);

/// <summary>§6.4 SQLite 存储（WAL、FTS5）。单连接 + 锁，读写都很短，避免多连接的忙等。</summary>
public sealed class LibraryDatabase : IDisposable
{
    internal const int SchemaVersion = 3;
    private readonly SqliteConnection _db;
    private readonly Lock _gate = new();

    private LibraryDatabase(SqliteConnection db) => _db = db;

    public static LibraryDatabase Open(string path)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (dir is not null) Directory.CreateDirectory(dir);
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();
        var db = new SqliteConnection(cs);
        db.Open();
        db.Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
        var store = new LibraryDatabase(db);
        store.Migrate();
        return store;
    }

    public static LibraryDatabase OpenInMemory()
    {
        var db = new SqliteConnection("Data Source=:memory:");
        db.Open();
        db.Execute("PRAGMA foreign_keys=ON;");
        var store = new LibraryDatabase(db);
        store.Migrate();
        return store;
    }

    public void Dispose() => _db.Dispose();

    private void Migrate()
    {
        var version = _db.ExecuteScalar<long>("PRAGMA user_version");
        if (version >= SchemaVersion) return;
        using var tx = _db.BeginTransaction();
        if (version == 0) CreateBaseSchema(tx);
        if (version == 1)
        {
            // 1 → 2：音频特征（聆听空间）。已有曲目的 features_ver 为 0，会在后台补算。
            _db.Execute("ALTER TABLE analysis ADD COLUMN features_ver INTEGER NOT NULL DEFAULT 0", transaction: tx);
        }
        // 2 → 3：远程歌单索引与本地匹配（网易云同步）。会话凭据从不进入数据库。
        CreateRemoteSchema(tx);
        _db.Execute($"PRAGMA user_version={SchemaVersion}", transaction: tx);
        tx.Commit();
    }

    /// <summary>schema 2 的全部表（新数据库从这里开始，再逐级升级）。</summary>
    private void CreateBaseSchema(SqliteTransaction tx)
    {
        _db.Execute("""
            CREATE TABLE roots(id INTEGER PRIMARY KEY, path TEXT NOT NULL UNIQUE COLLATE NOCASE, added_at TEXT NOT NULL, available INTEGER NOT NULL DEFAULT 1);
            CREATE TABLE tracks(
                id INTEGER PRIMARY KEY, root_id INTEGER NOT NULL REFERENCES roots(id) ON DELETE CASCADE,
                rel_path TEXT NOT NULL COLLATE NOCASE, size INTEGER NOT NULL, mtime INTEGER NOT NULL,
                title TEXT NOT NULL, artist TEXT NOT NULL, album_artist TEXT NOT NULL, album TEXT NOT NULL,
                disc INTEGER NOT NULL, track_no INTEGER NOT NULL, year INTEGER, duration_ms INTEGER NOT NULL,
                codec TEXT NOT NULL, sample_rate INTEGER NOT NULL, bit_depth INTEGER NOT NULL, channels INTEGER NOT NULL,
                cover_hash TEXT, available INTEGER NOT NULL DEFAULT 1,
                UNIQUE(root_id, rel_path));
            CREATE TABLE groups(id INTEGER PRIMARY KEY, kind TEXT NOT NULL, key TEXT NOT NULL, display_name TEXT NOT NULL, sort_key INTEGER NOT NULL, UNIQUE(kind, key));
            CREATE TABLE track_groups(track_id INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE, group_id INTEGER NOT NULL REFERENCES groups(id) ON DELETE CASCADE, order_in_group INTEGER NOT NULL, PRIMARY KEY(track_id, group_id));
            CREATE TABLE analysis(track_id INTEGER PRIMARY KEY REFERENCES tracks(id) ON DELETE CASCADE, waveform_ver INTEGER NOT NULL, palette_json TEXT, analyzed_at TEXT NOT NULL, features_ver INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE user_data(track_id INTEGER PRIMARY KEY REFERENCES tracks(id) ON DELETE CASCADE, play_count INTEGER NOT NULL DEFAULT 0, last_played_at TEXT, note TEXT, favorite INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE tags(id INTEGER PRIMARY KEY, name TEXT NOT NULL UNIQUE COLLATE NOCASE);
            CREATE TABLE track_tags(track_id INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE, tag_id INTEGER NOT NULL REFERENCES tags(id) ON DELETE CASCADE, PRIMARY KEY(track_id, tag_id));
            CREATE TABLE markers(id INTEGER PRIMARY KEY, track_id INTEGER NOT NULL REFERENCES tracks(id) ON DELETE CASCADE, position_ms INTEGER NOT NULL, label TEXT);
            CREATE TABLE session(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE VIRTUAL TABLE tracks_fts USING fts5(title, artist, album, rel_path, pinyin, tokenize='unicode61 remove_diacritics 2');
            CREATE INDEX ix_markers_track ON markers(track_id);
            """, transaction: tx);
    }

    /// <summary>
    /// schema 3：远程账号、歌单、曲目、歌单条目、匹配结果和同步记录。
    /// 远程歌单被删除时只标记 remote_deleted；本地曲目被删除时映射的 local_track_id 置空（显示为本地文件缺失）。
    /// </summary>
    private void CreateRemoteSchema(SqliteTransaction tx)
    {
        _db.Execute("""
            CREATE TABLE IF NOT EXISTS remote_accounts(
                id INTEGER PRIMARY KEY, provider TEXT NOT NULL, remote_user_id TEXT NOT NULL, nickname TEXT, avatar_url TEXT,
                last_sync_at TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL,
                UNIQUE(provider, remote_user_id));
            CREATE TABLE IF NOT EXISTS remote_playlists(
                provider TEXT NOT NULL, playlist_id TEXT NOT NULL,
                account_id INTEGER NOT NULL REFERENCES remote_accounts(id) ON DELETE CASCADE,
                name TEXT NOT NULL, description TEXT, cover_url TEXT, is_owned INTEGER NOT NULL, track_count INTEGER NOT NULL,
                remote_updated_at TEXT, synced_at TEXT NOT NULL, position INTEGER NOT NULL DEFAULT 0, remote_deleted INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY(provider, playlist_id));
            CREATE TABLE IF NOT EXISTS remote_tracks(
                provider TEXT NOT NULL, track_id TEXT NOT NULL, title TEXT NOT NULL, artist TEXT NOT NULL, album TEXT NOT NULL,
                duration_ms INTEGER NOT NULL, isrc TEXT, cover_url TEXT, playable_online INTEGER NOT NULL DEFAULT 0,
                remote_updated_at TEXT, raw_hash TEXT NOT NULL, synced_at TEXT NOT NULL,
                PRIMARY KEY(provider, track_id));
            CREATE TABLE IF NOT EXISTS remote_playlist_items(
                provider TEXT NOT NULL, playlist_id TEXT NOT NULL, track_id TEXT NOT NULL, position INTEGER NOT NULL, added_at TEXT,
                PRIMARY KEY(provider, playlist_id, track_id),
                FOREIGN KEY(provider, playlist_id) REFERENCES remote_playlists(provider, playlist_id) ON DELETE CASCADE,
                FOREIGN KEY(provider, track_id) REFERENCES remote_tracks(provider, track_id) ON DELETE CASCADE);
            CREATE TABLE IF NOT EXISTS remote_track_mappings(
                provider TEXT NOT NULL, track_id TEXT NOT NULL, local_track_id INTEGER, status TEXT NOT NULL,
                confidence REAL NOT NULL, reason TEXT, is_manual INTEGER NOT NULL DEFAULT 0, updated_at TEXT NOT NULL,
                PRIMARY KEY(provider, track_id),
                FOREIGN KEY(provider, track_id) REFERENCES remote_tracks(provider, track_id) ON DELETE CASCADE,
                FOREIGN KEY(local_track_id) REFERENCES tracks(id) ON DELETE SET NULL);
            CREATE TABLE IF NOT EXISTS sync_runs(
                id INTEGER PRIMARY KEY, provider TEXT NOT NULL, account_id INTEGER, started_at TEXT NOT NULL, finished_at TEXT,
                status TEXT NOT NULL, playlists_seen INTEGER NOT NULL DEFAULT 0, tracks_seen INTEGER NOT NULL DEFAULT 0,
                matched INTEGER NOT NULL DEFAULT 0, errors INTEGER NOT NULL DEFAULT 0, error_message TEXT);
            CREATE INDEX IF NOT EXISTS ix_remote_items_playlist ON remote_playlist_items(provider, playlist_id, position);
            CREATE INDEX IF NOT EXISTS ix_remote_mappings_local ON remote_track_mappings(local_track_id);
            """, transaction: tx);
    }

    /// <summary>同一程序集内的其他存储（远程歌单索引）共享这条连接和这把锁。</summary>
    internal T Locked<T>(Func<SqliteConnection, T> work)
    {
        lock (_gate) return work(_db);
    }

    internal void Locked(Action<SqliteConnection> work)
    {
        lock (_gate) work(_db);
    }

    // ───────────────────────── roots ─────────────────────────

    public LibraryRoot AddRoot(string path)
    {
        var full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        lock (_gate)
        {
            _db.Execute("INSERT OR IGNORE INTO roots(path, added_at) VALUES(@full, @at)", new { full, at = DateTimeOffset.UtcNow.ToString("O") });
            return _db.QuerySingle<RootRow>("SELECT id, path, added_at AS AddedAt FROM roots WHERE path=@full", new { full }).ToRoot();
        }
    }

    public IReadOnlyList<LibraryRoot> GetRoots()
    {
        lock (_gate) return _db.Query<RootRow>("SELECT id, path, added_at AS AddedAt FROM roots ORDER BY id").Select(r => r.ToRoot()).ToList();
    }

    public void RemoveRoot(long rootId)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            _db.Execute("DELETE FROM tracks_fts WHERE rowid IN (SELECT id FROM tracks WHERE root_id=@rootId)", new { rootId }, tx);
            _db.Execute("DELETE FROM roots WHERE id=@rootId", new { rootId }, tx);
            tx.Commit();
        }
    }

    // ───────────────────────── tracks ─────────────────────────

    public Dictionary<string, FileState> GetFileStates(long rootId)
    {
        lock (_gate)
        {
            return _db.Query<(string RelPath, long Id, long Size, long MTime, long Available)>(
                    "SELECT rel_path, id, size, mtime, available FROM tracks WHERE root_id=@rootId", new { rootId })
                .ToDictionary(r => r.RelPath, r => new FileState(r.Id, r.Size, r.MTime, r.Available != 0), StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>批量写入（新增或变化），返回每首歌的 id。一个事务内完成。</summary>
    public IReadOnlyList<TrackId> UpsertTracks(long rootId, IReadOnlyList<ScannedTrack> batch)
    {
        var ids = new List<TrackId>(batch.Count);
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            foreach (var t in batch)
            {
                var id = _db.QuerySingle<long>("""
                    INSERT INTO tracks(root_id, rel_path, size, mtime, title, artist, album_artist, album, disc, track_no, year,
                                       duration_ms, codec, sample_rate, bit_depth, channels, cover_hash, available)
                    VALUES(@rootId, @RelPath, @Size, @MTime, @Title, @Artist, @AlbumArtist, @Album, @Disc, @TrackNo, @Year,
                           @DurationMs, @Codec, @SampleRate, @BitDepth, @Channels, @CoverHash, 1)
                    ON CONFLICT(root_id, rel_path) DO UPDATE SET
                        size=excluded.size, mtime=excluded.mtime, title=excluded.title, artist=excluded.artist,
                        album_artist=excluded.album_artist, album=excluded.album, disc=excluded.disc, track_no=excluded.track_no,
                        year=excluded.year, duration_ms=excluded.duration_ms, codec=excluded.codec, sample_rate=excluded.sample_rate,
                        bit_depth=excluded.bit_depth, channels=excluded.channels, cover_hash=excluded.cover_hash, available=1
                    RETURNING id
                    """, new
                {
                    rootId, t.RelPath, t.Size, t.MTime,
                    t.Meta.Title, t.Meta.Artist, t.Meta.AlbumArtist, t.Meta.Album, t.Meta.Disc, t.Meta.TrackNo, t.Meta.Year,
                    DurationMs = (long)t.Meta.Duration.TotalMilliseconds,
                    t.Format.Codec, t.Format.SampleRate, t.Format.BitDepth, t.Format.Channels, t.CoverHash,
                }, tx);

                _db.Execute("DELETE FROM tracks_fts WHERE rowid=@id", new { id }, tx);
                _db.Execute("INSERT INTO tracks_fts(rowid, title, artist, album, rel_path, pinyin) VALUES(@id, @title, @artist, @album, @rel, @pinyin)", new
                {
                    id,
                    title = SearchText.Index(t.Meta.Title),
                    artist = SearchText.Index($"{t.Meta.Artist} {t.Meta.AlbumArtist}"),
                    album = SearchText.Index(t.Meta.Album),
                    rel = SearchText.Index(t.RelPath.Replace('\\', ' ').Replace('/', ' ')),
                    pinyin = Pinyin.SearchKey($"{t.Meta.Title} {t.Meta.Artist} {t.Meta.Album}"),
                }, tx);
                // 文件内容变化后旧的分析结果失效。
                _db.Execute("DELETE FROM analysis WHERE track_id=@id", new { id }, tx);
                ids.Add(new TrackId(id));
            }
            tx.Commit();
        }
        return ids;
    }

    public void SetAvailability(IEnumerable<TrackId> ids, bool available)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            foreach (var id in ids)
                _db.Execute("UPDATE tracks SET available=@a WHERE id=@id", new { a = available ? 1 : 0, id = id.Value }, tx);
            tx.Commit();
        }
    }

    public void SetRootAvailability(long rootId, bool available)
    {
        lock (_gate)
        {
            _db.Execute("UPDATE roots SET available=@a WHERE id=@rootId", new { a = available ? 1 : 0, rootId });
            if (!available) _db.Execute("UPDATE tracks SET available=0 WHERE root_id=@rootId", new { rootId });
        }
    }

    public IReadOnlyList<Track> LoadTracks()
    {
        lock (_gate)
        {
            var roots = _db.Query<RootRow>("SELECT id, path, added_at AS AddedAt FROM roots").ToDictionary(r => r.Id);
            return _db.Query<TrackRow>("SELECT * FROM tracks").Select(r => r.ToTrack(roots[r.root_id].Path)).ToList();
        }
    }

    public string? GetCoverHash(TrackId id)
    {
        lock (_gate) return _db.ExecuteScalar<string?>("SELECT cover_hash FROM tracks WHERE id=@id", new { id = id.Value });
    }

    public Dictionary<TrackId, string> GetAllCoverHashes()
    {
        lock (_gate)
            return _db.Query<(long Id, string Hash)>("SELECT id, cover_hash FROM tracks WHERE cover_hash IS NOT NULL")
                .ToDictionary(r => new TrackId(r.Id), r => r.Hash);
    }

    public LibraryIndex LoadIndex() => LibraryIndex.From(LoadTracks(), LoadUserData());

    /// <summary>重建 groups / track_groups（文件夹与专辑），排序规则与 Core 完全一致。</summary>
    public void RebuildGroups(LibraryIndex index)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            _db.Execute("DELETE FROM track_groups; DELETE FROM groups;", transaction: tx);
            foreach (var (arr, kind) in new[] { (Arrangement.Folder, "folder"), (Arrangement.Album, "album") })
            {
                var ctx = index.Arrange(arr);
                for (int g = 0; g < ctx.Groups.Length; g++)
                {
                    var group = ctx.Groups[g];
                    var gid = _db.QuerySingle<long>("INSERT INTO groups(kind, key, display_name, sort_key) VALUES(@kind, @Key, @DisplayName, @g) RETURNING id",
                        new { kind, group.Key, group.DisplayName, g }, tx);
                    for (int i = 0; i < group.Count; i++)
                        _db.Execute("INSERT OR IGNORE INTO track_groups(track_id, group_id, order_in_group) VALUES(@t, @gid, @i)",
                            new { t = ctx.Order[group.Start + i].Value, gid, i }, tx);
                }
            }
            tx.Commit();
        }
    }

    // ───────────────────────── 搜索 ─────────────────────────

    public ImmutableArray<TrackId> Search(string query, int limit = 1000)
    {
        // 前缀词同时匹配所有列，包括拼音列（"qt"*、"qingt"* 命中 "qingtian qt"）。
        var expr = SearchText.MatchQuery(query);
        if (expr is null) return [];

        lock (_gate)
        {
            return [.. _db.Query<long>(
                "SELECT t.id FROM tracks_fts f JOIN tracks t ON t.id=f.rowid WHERE tracks_fts MATCH @expr ORDER BY bm25(tracks_fts, 10, 5, 4, 1, 3), t.title LIMIT @limit",
                new { expr, limit }).Select(id => new TrackId(id))];
        }
    }

    // ───────────────────────── 用户数据 ─────────────────────────

    public Dictionary<TrackId, UserTrackData> LoadUserData()
    {
        lock (_gate)
        {
            return _db.Query<(long TrackId, long PlayCount, string? LastPlayedAt, long Favorite)>(
                    "SELECT track_id, play_count, last_played_at, favorite FROM user_data")
                .ToDictionary(r => new TrackId(r.TrackId), r => new UserTrackData(
                    (int)r.PlayCount,
                    r.LastPlayedAt is null ? null : DateTimeOffset.Parse(r.LastPlayedAt, System.Globalization.CultureInfo.InvariantCulture),
                    r.Favorite != 0));
        }
    }

    public UserTrackData RecordPlay(TrackId id, DateTimeOffset at)
    {
        lock (_gate)
        {
            _db.Execute("""
                INSERT INTO user_data(track_id, play_count, last_played_at) VALUES(@id, 1, @at)
                ON CONFLICT(track_id) DO UPDATE SET play_count=play_count+1, last_played_at=excluded.last_played_at
                """, new { id = id.Value, at = at.ToString("O") });
        }
        return LoadUserData()[id];
    }

    public void SetFavorite(TrackId id, bool favorite)
    {
        lock (_gate)
        {
            _db.Execute("INSERT INTO user_data(track_id, favorite) VALUES(@id, @f) ON CONFLICT(track_id) DO UPDATE SET favorite=excluded.favorite",
                new { id = id.Value, f = favorite ? 1 : 0 });
        }
    }

    public void SetNote(TrackId id, string note)
    {
        lock (_gate)
        {
            _db.Execute("INSERT INTO user_data(track_id, note) VALUES(@id, @note) ON CONFLICT(track_id) DO UPDATE SET note=excluded.note",
                new { id = id.Value, note });
        }
    }

    public string? GetNote(TrackId id)
    {
        lock (_gate) return _db.ExecuteScalar<string?>("SELECT note FROM user_data WHERE track_id=@id", new { id = id.Value });
    }

    public void SetTags(TrackId id, IEnumerable<string> tags)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction();
            _db.Execute("DELETE FROM track_tags WHERE track_id=@id", new { id = id.Value }, tx);
            foreach (var name in tags.Select(t => t.Trim()).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _db.Execute("INSERT OR IGNORE INTO tags(name) VALUES(@name)", new { name }, tx);
                _db.Execute("INSERT OR IGNORE INTO track_tags(track_id, tag_id) SELECT @id, id FROM tags WHERE name=@name", new { id = id.Value, name }, tx);
            }
            _db.Execute("DELETE FROM tags WHERE id NOT IN (SELECT tag_id FROM track_tags)", transaction: tx);
            tx.Commit();
        }
    }

    public IReadOnlyList<string> GetTags(TrackId id)
    {
        lock (_gate)
            return _db.Query<string>("SELECT g.name FROM track_tags t JOIN tags g ON g.id=t.tag_id WHERE t.track_id=@id ORDER BY g.name", new { id = id.Value }).ToList();
    }

    public Marker AddMarker(TrackId id, TimeSpan position, string? label)
    {
        lock (_gate)
        {
            var mid = _db.QuerySingle<long>("INSERT INTO markers(track_id, position_ms, label) VALUES(@id, @ms, @label) RETURNING id",
                new { id = id.Value, ms = (long)position.TotalMilliseconds, label });
            return new Marker(mid, id, TimeSpan.FromMilliseconds((long)position.TotalMilliseconds), label);
        }
    }

    public void SetMarkerLabel(long markerId, string? label)
    {
        lock (_gate) _db.Execute("UPDATE markers SET label=@label WHERE id=@markerId", new { markerId, label });
    }

    public void DeleteMarker(long markerId)
    {
        lock (_gate) _db.Execute("DELETE FROM markers WHERE id=@markerId", new { markerId });
    }

    public IReadOnlyList<Marker> GetMarkers(TrackId id)
    {
        lock (_gate)
        {
            return _db.Query<(long Id, long PositionMs, string? Label)>("SELECT id, position_ms, label FROM markers WHERE track_id=@id ORDER BY position_ms", new { id = id.Value })
                .Select(r => new Marker(r.Id, id, TimeSpan.FromMilliseconds(r.PositionMs), r.Label)).ToList();
        }
    }

    // ───────────────────────── 分析 ─────────────────────────

    public void SaveAnalysis(TrackId id, int waveformVersion, int featuresVersion, Palette? palette)
    {
        var json = palette is null ? null : JsonSerializer.Serialize(new PaletteDto(
            palette.Base.ToHex(), palette.Accent.ToHex(), palette.Dim.ToHex(), palette.TextOnBase.ToHex(),
            [.. palette.Swatches.ToList().Select(c => c.ToHex())]));
        lock (_gate)
        {
            _db.Execute("INSERT OR REPLACE INTO analysis(track_id, waveform_ver, features_ver, palette_json, analyzed_at) VALUES(@id, @waveformVersion, @featuresVersion, @json, @at)",
                new { id = id.Value, waveformVersion, featuresVersion, json, at = DateTimeOffset.UtcNow.ToString("O") });
        }
    }

    public (int WaveformVersion, int FeaturesVersion, Palette? Palette)? GetAnalysis(TrackId id)
    {
        lock (_gate)
        {
            var row = _db.QuerySingleOrDefault<(long Ver, long FeaturesVer, string? Json)?>(
                "SELECT waveform_ver, features_ver, palette_json FROM analysis WHERE track_id=@id", new { id = id.Value });
            if (row is not { } r) return null;
            Palette? palette = null;
            if (r.Json is not null && JsonSerializer.Deserialize<PaletteDto>(r.Json) is { } d)
            {
                palette = new Palette(Rgb.FromHex(d.Base), Rgb.FromHex(d.Accent), Rgb.FromHex(d.Dim), Rgb.FromHex(d.TextOnBase),
                    d.Swatches is { Length: > 0 } sw ? SwatchSet.Of([.. sw.Select(Rgb.FromHex)]) : default);
            }
            return ((int)r.Ver, (int)r.FeaturesVer, palette);
        }
    }

    /// <summary>需要（重新）分析的曲目：没有分析结果，或波形 / 特征的版本过期。</summary>
    public IReadOnlyList<TrackId> GetUnanalyzed(int waveformVersion, int featuresVersion)
    {
        lock (_gate)
        {
            return _db.Query<long>(
                    "SELECT t.id FROM tracks t LEFT JOIN analysis a ON a.track_id=t.id WHERE t.available=1 AND (a.track_id IS NULL OR a.waveform_ver<>@waveformVersion OR a.features_ver<>@featuresVersion)",
                    new { waveformVersion, featuresVersion }).Select(id => new TrackId(id)).ToList();
        }
    }

    // ───────────────────────── 会话 ─────────────────────────

    public void SaveSession(SessionSnapshot s)
    {
        var dto = new SessionDto(s.Track.Value, (int)s.ContextSnapshot.Arrangement, (int)s.ContextSnapshot.Scope.Kind, s.ContextSnapshot.Scope.Key,
            [.. s.ContextSnapshot.Order.Select(t => t.Value)],
            [.. s.ContextSnapshot.Groups.Select(g => new GroupDto(g.Key, g.DisplayName, g.Start, g.Count))],
            s.IndexInContext, (long)s.Position.TotalMilliseconds, (int)s.Repeat, (int)s.Arrangement);
        SetSessionValue("playing", JsonSerializer.Serialize(dto));
    }

    public SessionSnapshot? LoadSession()
    {
        var json = GetSessionValue("playing");
        if (json is null) return null;
        try
        {
            var d = JsonSerializer.Deserialize<SessionDto>(json);
            if (d is null) return null;
            var ctx = new PlaybackContext((Arrangement)d.ContextArrangement, new ScopeKey((ScopeKind)d.ScopeKind, d.ScopeKey),
                [.. d.Order.Select(v => new TrackId(v))], [.. d.Groups.Select(g => new ContextGroup(g.Key, g.Display, g.Start, g.Count))]);
            return new SessionSnapshot(new TrackId(d.Track), ctx, d.Index, TimeSpan.FromMilliseconds(d.PositionMs), (RepeatMode)d.Repeat, (Arrangement)d.Arrangement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void SetSessionValue(string key, string value)
    {
        lock (_gate) _db.Execute("INSERT OR REPLACE INTO session(key, value) VALUES(@key, @value)", new { key, value });
    }

    public string? GetSessionValue(string key)
    {
        lock (_gate) return _db.ExecuteScalar<string?>("SELECT value FROM session WHERE key=@key", new { key });
    }

    // ───────────────────────── 行映射 ─────────────────────────

    private sealed record PaletteDto(string Base, string Accent, string Dim, string TextOnBase, string[]? Swatches = null);
    private sealed record GroupDto(string Key, string Display, int Start, int Count);
    private sealed record SessionDto(long Track, int ContextArrangement, int ScopeKind, string ScopeKey, long[] Order, GroupDto[] Groups,
        int Index, long PositionMs, int Repeat, int Arrangement);

    private sealed class RootRow
    {
        public long Id { get; init; }
        public string Path { get; init; } = "";
        public string AddedAt { get; init; } = "";
        public LibraryRoot ToRoot() => new(Id, Path, DateTimeOffset.Parse(AddedAt, System.Globalization.CultureInfo.InvariantCulture));
    }

#pragma warning disable IDE1006 // 列名映射
    private sealed class TrackRow
    {
        public long id { get; init; }
        public long root_id { get; init; }
        public string rel_path { get; init; } = "";
        public string title { get; init; } = "";
        public string artist { get; init; } = "";
        public string album_artist { get; init; } = "";
        public string album { get; init; } = "";
        public long disc { get; init; }
        public long track_no { get; init; }
        public long? year { get; init; }
        public long duration_ms { get; init; }
        public string codec { get; init; } = "";
        public long sample_rate { get; init; }
        public long bit_depth { get; init; }
        public long channels { get; init; }
        public long available { get; init; }

        public Track ToTrack(string rootPath)
        {
            var relDir = System.IO.Path.GetDirectoryName(rel_path) ?? "";
            var folderDisplay = relDir.Length == 0 ? System.IO.Path.GetFileName(rootPath) : System.IO.Path.GetFileName(relDir);
            var (albumKey, albumDisplay) = Arrangements.AlbumKey(album_artist, artist, album);
            return new Track(
                new TrackId(id),
                System.IO.Path.Combine(rootPath, rel_path),
                new TrackMeta(title, artist, album_artist, album, (int)disc, (int)track_no, (int?)year, TimeSpan.FromMilliseconds(duration_ms)),
                new AudioFormat(codec, (int)sample_rate, (int)bit_depth, (int)channels),
                new GroupKeys($"{root_id}:{relDir.Replace('\\', '/')}", folderDisplay, albumKey, albumDisplay),
                available != 0);
        }
    }
#pragma warning restore IDE1006
}
