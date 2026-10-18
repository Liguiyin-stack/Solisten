using System.Collections.Concurrent;
using Solisten.Core.Model;
using Solisten.Library.Analysis;
using Solisten.Library.Metadata;
using Solisten.Library.Storage;

namespace Solisten.Library.Scanning;

public sealed record ScanProgress(int Processed, int Total);

public sealed record ScanResult(
    LibraryRoot Root,
    bool RootAvailable,
    IReadOnlyList<TrackId> AddedOrChanged,
    IReadOnlyList<TrackId> Removed,
    IReadOnlyList<TrackId> Restored,
    int Unchanged,
    IReadOnlyList<string> Unsupported)
{
    public bool HasChanges => AddedOrChanged.Count + Removed.Count + Restored.Count > 0 || !RootAvailable;
}

/// <summary>§6.1 ①–⑤：按（路径, 修改时间, 大小）判断新增/变化/删除，读取元数据与封面，每 500 首提交一次。</summary>
public sealed class LibraryScanner(LibraryDatabase db, AppPaths paths)
{
    public const int BatchSize = 500;

    /// <summary>FFmpeg 能解码、但 MVP 尚未支持的格式：在设置里列为"未导入的文件"。</summary>
    public static readonly IReadOnlySet<string> KnownUnsupported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".ape", ".wv", ".dsf", ".dff", ".wma", ".mka", ".tta", ".tak", ".mpc",
    };

    public ScanResult Scan(LibraryRoot root, IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var existing = db.GetFileStates(root.Id);
        if (!Directory.Exists(root.Path))
        {
            // 根目录离线（例如移动硬盘拔出）：整组标记为不可用，重新连接后自动恢复。
            db.SetRootAvailability(root.Id, false);
            return new ScanResult(root, false, [], [.. existing.Values.Where(s => s.Available).Select(s => new TrackId(s.Id))], [], 0, []);
        }
        db.SetRootAvailability(root.Id, true);

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        var unsupported = new List<string>();
        var toRead = new List<(string Full, string Rel, long Size, long MTime)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var restored = new List<TrackId>();
        int unchanged = 0;

        foreach (var file in new DirectoryInfo(root.Path).EnumerateFiles("*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (!TrackMetadataReader.IsSupported(file.FullName))
            {
                if (KnownUnsupported.Contains(file.Extension)) unsupported.Add(file.FullName);
                continue;
            }

            var rel = Path.GetRelativePath(root.Path, file.FullName);
            seen.Add(rel);
            long size = file.Length;
            long mtime = file.LastWriteTimeUtc.Ticks;
            if (existing.TryGetValue(rel, out var state) && state.Size == size && state.MTime == mtime)
            {
                if (state.Available) unchanged++;
                else restored.Add(new TrackId(state.Id));
                continue;
            }
            toRead.Add((file.FullName, rel, size, mtime));
        }

        if (restored.Count > 0) db.SetAvailability(restored, true);

        var added = new List<TrackId>();
        var folderCovers = new ConcurrentDictionary<string, Lazy<string?>>(StringComparer.OrdinalIgnoreCase);
        int processed = 0;
        foreach (var chunk in toRead.Chunk(BatchSize))
        {
            var scanned = new ScannedTrack[chunk.Length];
            Parallel.For(0, chunk.Length, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
            {
                var (full, rel, size, mtime) = chunk[i];
                var md = TrackMetadataReader.Read(full, root.Path);
                var hash = md.Cover is { } c
                    ? StoreCover(c.Data, c.MimeType)
                    : folderCovers.GetOrAdd(Path.GetDirectoryName(full)!, d => new Lazy<string?>(() => FolderCoverHash(d))).Value;
                scanned[i] = new ScannedTrack(rel, size, mtime, md.Meta, md.Format, hash);
                progress?.Report(new ScanProgress(Interlocked.Increment(ref processed), toRead.Count));
            });
            added.AddRange(db.UpsertTracks(root.Id, scanned));
        }

        var removed = existing.Where(kv => kv.Value.Available && !seen.Contains(kv.Key)).Select(kv => new TrackId(kv.Value.Id)).ToList();
        if (removed.Count > 0) db.SetAvailability(removed, false);

        return new ScanResult(root, true, added, removed, restored, unchanged, unsupported);
    }

    private string? FolderCoverHash(string directory)
    {
        if (CoverLocator.FindFolderCover(directory) is not { } path) return null;
        try
        {
            var bytes = File.ReadAllBytes(path);
            return StoreCover(bytes, Path.GetExtension(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>把封面以内容哈希存入 cache\cover\，相同封面只存一份（同一专辑的多首歌并行扫描时也只写一次）。</summary>
    private string StoreCover(byte[] data, string mime)
    {
        var hash = CoverLocator.Hash(data);
        _ = _storedCovers.GetOrAdd(hash, h => new Lazy<bool>(() =>
        {
            if (paths.FindCover(h) is null)
                AtomicFile.WriteAllBytes(paths.CoverPath(h, CoverLocator.ExtensionFor(mime, data)), data);
            return true;
        })).Value;
        return hash;
    }

    private readonly ConcurrentDictionary<string, Lazy<bool>> _storedCovers = new(StringComparer.Ordinal);
}
