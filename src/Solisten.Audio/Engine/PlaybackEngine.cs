using System.Collections.Concurrent;
using System.Diagnostics;
using Solisten.Audio.Decoding;
using Solisten.Core.Model;

namespace Solisten.Audio.Engine;

/// <summary>
/// §5 播放引擎。
/// <list type="bullet">
/// <item>引擎线程：执行控制命令、FFmpeg 解码、写入环形缓冲区与边界标记、管理输出设备的生命周期。</item>
/// <item>渲染线程（由 <see cref="IAudioOutput"/> 驱动）：只从环形缓冲区拷贝数据、乘以增益和淡化包络，
///   处理边界标记并更新时钟；不分配内存、不加锁、不做 I/O。</item>
/// <item>通知线程：把渲染线程产生的事件（无缝切歌、播放结束）转发给外部。</item>
/// </list>
/// 无缝播放：当前曲目 EOF 后立即从后继曲目继续写入同一个缓冲区，并在全局采样序号处写入边界标记；
/// 渲染线程消费到标记时发出 <see cref="TrackBoundaryReached"/>，界面与声音在同一帧切换。
/// </summary>
public sealed class PlaybackEngine : IRenderSource, IDisposable
{
    public static readonly TimeSpan BufferLength = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan PreopenBeforeEnd = TimeSpan.FromSeconds(10);
    private const double SeekFadeSeconds = 0.005;
    private const double PauseFadeSeconds = 0.010;
    private const double VolumeRampSeconds = 0.020;
    private const int DecodeChunkFrames = 4096;

    private readonly AudioOutputFactory _outputFactory;
    private readonly Func<string, int, int, ISeekablePcmReader> _openReader;
    private readonly ConcurrentQueue<Command> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly AutoResetEvent _flushDone = new(false);
    private readonly AutoResetEvent _notify = new(false);
    private readonly SpscQueue<RenderEvent> _renderEvents = new(64);
    private readonly ConcurrentQueue<Action> _engineEvents = new();
    private readonly Thread _engineThread;
    private readonly Thread _notifyThread;
    private volatile bool _disposed;

    // —— 引擎线程拥有 ——
    private IAudioOutput? _output;
    private SpscRingBuffer _ring = new(1);
    private SpscQueue<Marker> _markers = new(64);
    private int _channels = 2;
    private int _rate = 48000;
    private Segment? _current;
    private (TrackId Id, string Path)? _successor;
    private ISeekablePcmReader? _preopened;
    private TrackId? _preopenedId;
    private float[] _decodeBuf = [];
    private int _decodeOffset;
    private int _decodeCount;
    private volatile bool _outputRunning;
    private volatile bool _decoderFinished = true;

    // —— 跨线程控制 ——
    private volatile bool _wantPlaying;
    private volatile float _targetGain = 1f;
    private int _flushRequest;
    private int _flushAck;

    // —— 渲染线程拥有 ——
    private float _gain = 1f;
    private float _fade;
    private bool _postFlushFade;
    private bool _ended = true;
    private long _segTrack = -1;
    private long _segStartFrame;
    private long _segOffsetFrames;

    // —— 时钟（seqlock：渲染线程写，任意线程读）——
    private int _clkVersion;
    private long _clkTrack = -1;
    private long _clkSegStart;
    private long _clkOffset;
    private long _clkPlayed;
    private long _clkConsumed;
    private long _clkStamp;
    private int _clkRate = 48000;
    private bool _clkRunning;

    public PlaybackEngine(AudioOutputFactory outputFactory, Func<string, int, int, ISeekablePcmReader>? openReader = null)
    {
        _outputFactory = outputFactory;
        var open = openReader ?? ((path, rate, ch) => PcmReaderFactory.Default.Open(path, rate, ch));
        _openReader = (path, rate, ch) =>
        {
            try { return open(path, rate, ch); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new AudioDecodeException(e is FileNotFoundException or DirectoryNotFoundException ? "文件不存在" : e.Message);
            }
        };
        _engineThread = new Thread(EngineLoop) { IsBackground = true, Name = "Solisten engine" };
        _notifyThread = new Thread(NotifyLoop) { IsBackground = true, Name = "Solisten engine events" };
        _engineThread.Start();
        _notifyThread.Start();
    }

    /// <summary>已无缝衔接到后继曲目（参数是新曲目）。</summary>
    public event Action<TrackId>? TrackBoundaryReached;
    /// <summary>没有后继曲目，当前曲目最后一个采样已经送出。</summary>
    public event Action<TrackId>? TrackEnded;
    public event Action<TrackId, string>? PlaybackFailed;
    /// <summary>输出设备不可用。</summary>
    public event Action<string>? DeviceLost;
    /// <summary>输出设备已重建（默认设备变化或设备恢复）。</summary>
    public event Action<string>? DeviceChanged;

    public string? DeviceName => _output?.DeviceName;

    /// <summary>线性增益 0..1，渲染线程以 20ms 斜坡跟随。</summary>
    public float Volume
    {
        get => _targetGain;
        set => _targetGain = Math.Clamp(value, 0f, 1f);
    }

    // ───────────────────────── 控制 API（任意线程） ─────────────────────────

    public void Load(TrackId track, string path, TimeSpan startAt, bool paused) => Post(new LoadCmd(track, path, startAt, paused));
    public void SetSuccessor(TrackId? track, string? path) => Post(new SuccessorCmd(track is { } t && path is not null ? (t, path) : null));
    public void Seek(TimeSpan position) => Post(new SeekCmd(position));
    public void Pause() => Post(new PauseCmd(true));
    public void Resume() => Post(new PauseCmd(false));
    public void Stop() => Post(new StopCmd());

    /// <summary>默认设备变化：在新设备上重建输出并从当前位置继续。</summary>
    public void ReopenOutput() => Post(new ReopenCmd());

    private void Post(Command c)
    {
        if (_disposed) return;
        _commands.Enqueue(c);
        _wake.Set();
    }

    /// <summary>读取音频时钟（无锁，可在渲染层每帧调用）。</summary>
    public PlaybackClock Clock
    {
        get
        {
            while (true)
            {
                int v1 = Volatile.Read(ref _clkVersion);
                if ((v1 & 1) != 0) { Thread.SpinWait(4); continue; }
                long track = _clkTrack, segStart = _clkSegStart, offset = _clkOffset, played = _clkPlayed, consumed = _clkConsumed, stamp = _clkStamp;
                int rate = _clkRate;
                bool running = _clkRunning;
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _clkVersion) != v1) continue;

                if (track < 0) return new PlaybackClock(null, 0, rate, false);
                long pos = played;
                if (running)
                {
                    // 两次渲染回调之间按真实时间插值，但不超过已经送入设备的位置。
                    long elapsed = (Stopwatch.GetTimestamp() - stamp) * rate / Stopwatch.Frequency;
                    pos = Math.Min(played + elapsed, consumed);
                }
                long inTrack = offset + Math.Max(0, pos - segStart);
                return new PlaybackClock(new TrackId(track), inTrack, rate, running);
            }
        }
    }

    // ───────────────────────── 渲染线程 ─────────────────────────

    void IRenderSource.Render(Span<float> buffer, int queuedFramesAfterWrite) => Render(buffer, queuedFramesAfterWrite);

    internal void Render(Span<float> buffer, int queuedFramesAfterWrite)
    {
        int ch = _channels;
        int frames = buffer.Length / ch;
        float pauseStep = (float)(1.0 / (PauseFadeSeconds * _rate));
        float seekStep = (float)(1.0 / (SeekFadeSeconds * _rate));
        float gainStep = (float)(1.0 / (VolumeRampSeconds * _rate));
        bool flushPending = Volatile.Read(ref _flushRequest) != Volatile.Read(ref _flushAck);

        int f = 0;
        while (f < frames)
        {
            // 处理所有已经到达的标记（暂停时也处理，使时钟在加载后立刻指向新曲目）。
            if (!flushPending) ProcessMarkers();

            bool wantSound = _wantPlaying && !flushPending && !_ended;
            if (flushPending && _fade <= 0f)
            {
                DoFlush();
                flushPending = false;
                continue;
            }
            if (!wantSound && _fade <= 0f) break;

            // 本段最多读到下一个标记处，保证标记在正确的采样位置生效。
            int chunk = frames - f;
            if (!flushPending && _markers.TryPeek(out var next))
            {
                long until = (next.Sample - _ring.TotalRead) / ch;
                if (until <= 0) continue;
                chunk = (int)Math.Min(chunk, until);
            }

            var dst = buffer.Slice(f * ch, chunk * ch);
            int got = _ring.Read(dst) / ch;
            if (got == 0)
            {
                if (flushPending) { _fade = 0f; continue; }
                break; // 欠载：剩余写静音
            }

            for (int i = 0; i < got; i++)
            {
                float target = wantSound ? 1f : 0f;
                float step = flushPending || _postFlushFade ? seekStep : pauseStep;
                if (_fade < target) { _fade = Math.Min(target, _fade + step); if (_fade >= 1f) _postFlushFade = false; }
                else if (_fade > target) _fade = Math.Max(target, _fade - step);

                float tg = _targetGain;
                if (_gain < tg) _gain = Math.Min(tg, _gain + gainStep);
                else if (_gain > tg) _gain = Math.Max(tg, _gain - gainStep);

                float g = _gain * _fade;
                int o = i * ch;
                for (int c = 0; c < ch; c++) dst[o + c] *= g;
            }
            f += got;
            if (!wantSound && _fade <= 0f && !flushPending) break;
        }

        if (f < frames) buffer[(f * ch)..].Clear();
        if (!flushPending) ProcessMarkers();

        UpdateClock(queuedFramesAfterWrite);
        _wake.Set();
    }

    private void ProcessMarkers()
    {
        while (_markers.TryPeek(out var m) && m.Sample <= _ring.TotalRead)
        {
            _markers.TryDequeue(out _);
            switch (m.Kind)
            {
                case MarkerKind.Start:
                case MarkerKind.Boundary:
                    _segTrack = m.Track;
                    _segStartFrame = m.Sample / _channels;
                    _segOffsetFrames = m.OffsetFrames;
                    _ended = false;
                    if (m.Kind == MarkerKind.Boundary) RaiseFromRender(new RenderEvent(RenderEventKind.Boundary, m.Track));
                    break;
                case MarkerKind.End:
                    _ended = true;
                    RaiseFromRender(new RenderEvent(RenderEventKind.Ended, m.Track));
                    break;
            }
        }
    }

    private void DoFlush()
    {
        _ring.DiscardAll();
        _markers.Clear();
        _fade = 0f;
        _postFlushFade = true;
        Volatile.Write(ref _flushAck, Volatile.Read(ref _flushRequest));
        _flushDone.Set();
    }

    private void UpdateClock(int queuedAfter)
    {
        long consumed = _ring.TotalRead / _channels;
        Volatile.Write(ref _clkVersion, _clkVersion + 1);
        _clkTrack = _segTrack;
        _clkSegStart = _segStartFrame;
        _clkOffset = _segOffsetFrames;
        _clkConsumed = consumed;
        _clkPlayed = Math.Max(_segStartFrame, consumed - queuedAfter);
        _clkStamp = Stopwatch.GetTimestamp();
        _clkRate = _rate;
        _clkRunning = _wantPlaying && !_ended && _segTrack >= 0 && _ring.Available > 0;
        Volatile.Write(ref _clkVersion, _clkVersion + 1);
    }

    private void RaiseFromRender(in RenderEvent e)
    {
        _renderEvents.TryEnqueue(e);
        _notify.Set();
    }

    // ───────────────────────── 引擎线程 ─────────────────────────

    private void EngineLoop()
    {
        while (!_disposed)
        {
            while (_commands.TryDequeue(out var cmd))
            {
                try { Execute(cmd); }
                catch (AudioDecodeException e) { FailCurrent(e.Message); }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    PostEvent(() => DeviceLost?.Invoke(e.Message));
                }
            }

            bool didWork = false;
            try { didWork = DecodeStep(); }
            catch (AudioDecodeException e) { FailCurrent(e.Message); }

            if (!didWork) _wake.WaitOne(20);
        }
    }

    private void Execute(Command cmd)
    {
        switch (cmd)
        {
            case LoadCmd l:
                if (!EnsureOutput()) { PostEvent(() => DeviceLost?.Invoke("没有可用的输出设备")); return; }
                Flush();
                CloseCurrent();
                _wantPlaying = !l.Paused;
                try
                {
                    var reader = _openReader(l.Path, _rate, _channels);
                    if (l.StartAt > TimeSpan.Zero) reader.Seek(l.StartAt);
                    _current = new Segment(l.Track, l.Path, reader);
                    WriteMarker(MarkerKind.Start, l.Track, reader.PositionFrames);
                }
                catch (AudioDecodeException e)
                {
                    PostEvent(() => PlaybackFailed?.Invoke(l.Track, e.Message));
                }
                break;

            case SeekCmd s:
                if (_current is null) return;
                Flush();
                _current.Reader.Seek(s.Position);
                _current.Ended = false;
                _decodeCount = 0;
                WriteMarker(MarkerKind.Start, _current.Track, _current.Reader.PositionFrames);
                break;

            case PauseCmd p:
                if (!p.Paused && !EnsureOutput()) { PostEvent(() => DeviceLost?.Invoke("没有可用的输出设备")); return; }
                _wantPlaying = !p.Paused;
                break;

            case SuccessorCmd s:
                if (_successor?.Id != s.Next?.Id || _successor?.Path != s.Next?.Path)
                {
                    _successor = s.Next;
                    DisposePreopened();
                }
                break;

            case StopCmd:
                _wantPlaying = false;
                Flush();
                CloseCurrent();
                break;

            case ReopenCmd:
                ReopenOutputCore();
                break;
        }
    }

    /// <summary>请求渲染线程淡出并清空缓冲区，等待确认。输出未运行时直接在本线程清空。</summary>
    private void Flush()
    {
        _decodeCount = 0;
        if (!_outputRunning)
        {
            _ring.DiscardAll();
            _markers.Clear();
            _fade = 0f;
            _postFlushFade = true;
            return;
        }
        int req = Interlocked.Increment(ref _flushRequest);
        var sw = Stopwatch.StartNew();
        while (Volatile.Read(ref _flushAck) != req)
        {
            _flushDone.WaitOne(10);
            if (sw.ElapsedMilliseconds > 500 || !_outputRunning)
            {
                // 渲染线程没有响应（设备卡住）：停止输出后由本线程清空。
                StopOutput();
                _ring.DiscardAll();
                _markers.Clear();
                Volatile.Write(ref _flushAck, req);
                break;
            }
        }
    }

    private bool DecodeStep()
    {
        var seg = _current;
        if (seg is null || seg.Ended || _output is null)
        {
            _decoderFinished = true;
            return false;
        }
        _decoderFinished = false;

        // 先写完上次没写进去的数据
        if (_decodeCount > 0)
        {
            int w = _ring.Write(_decodeBuf.AsSpan(_decodeOffset, _decodeCount));
            _decodeOffset += w;
            _decodeCount -= w;
            return w > 0;
        }

        if (_ring.Free < DecodeChunkFrames * _channels / 4) return false;

        MaybePreopen(seg);

        int want = DecodeChunkFrames * _channels;
        if (_decodeBuf.Length < want) _decodeBuf = new float[want];
        int n = seg.Reader.Read(_decodeBuf.AsSpan(0, want));
        if (n > 0)
        {
            int w = _ring.Write(_decodeBuf.AsSpan(0, n));
            _decodeOffset = w;
            _decodeCount = n - w;
            return true;
        }

        // EOF：无缝衔接后继，或写入结束标记。
        seg.Ended = true;
        if (_successor is { } next)
        {
            ISeekablePcmReader? reader = _preopened is not null && _preopenedId == next.Id ? _preopened : null;
            _preopened = null;
            _preopenedId = null;
            try
            {
                reader ??= _openReader(next.Path, _rate, _channels);
            }
            catch (AudioDecodeException)
            {
                // 后继打不开：先正常结束当前曲目，由 Store 处理下一首（会再次尝试并报告失败）。
                reader = null;
            }

            if (reader is not null)
            {
                seg.Reader.Dispose();
                _current = new Segment(next.Id, next.Path, reader);
                _successor = null;
                WriteMarker(MarkerKind.Boundary, next.Id, 0);
                return true;
            }
        }
        WriteMarker(MarkerKind.End, seg.Track, 0);
        return false;
    }

    private void MaybePreopen(Segment seg)
    {
        if (_successor is not { } next || _preopenedId == next.Id) return;
        var duration = seg.Reader.Duration;
        if (duration <= TimeSpan.Zero) return;
        var remaining = duration - TimeSpan.FromSeconds(seg.Reader.PositionFrames / (double)_rate);
        if (remaining > PreopenBeforeEnd) return;

        DisposePreopened();
        try
        {
            _preopened = _openReader(next.Path, _rate, _channels);
            _preopenedId = next.Id;
        }
        catch (AudioDecodeException)
        {
            _preopenedId = next.Id; // 记住失败，EOF 时再处理
        }
    }

    private void WriteMarker(MarkerKind kind, TrackId track, long offsetFrames)
    {
        _markers.TryEnqueue(new Marker(_ring.TotalWritten, kind, track.Value, offsetFrames));
    }

    /// <summary>
    /// 解码中途失败：报告失败，并在已写入数据之后放一个结束标记。
    /// 如果失败的是已经预先写入缓冲区的后继曲目，Store 仍会先收到边界事件、再收到结束事件，不会卡在静音里。
    /// </summary>
    private void FailCurrent(string message)
    {
        if (_current is not { } seg) return;
        var track = seg.Track;
        if (!seg.Ended) WriteMarker(MarkerKind.End, track, 0);
        CloseCurrent();
        PostEvent(() => PlaybackFailed?.Invoke(track, message));
    }

    private void CloseCurrent()
    {
        _current?.Reader.Dispose();
        _current = null;
        _decodeCount = 0;
        DisposePreopened();
    }

    private void DisposePreopened()
    {
        _preopened?.Dispose();
        _preopened = null;
        _preopenedId = null;
    }

    // ───────────────────────── 输出设备 ─────────────────────────

    private bool EnsureOutput()
    {
        if (_output is not null && _outputRunning) return true;
        try
        {
            StopOutput();
            var output = _outputFactory(this);
            _rate = output.SampleRate;
            _channels = output.Channels;
            _ring = new SpscRingBuffer((int)(BufferLength.TotalSeconds * _rate * _channels));
            _markers = new SpscQueue<Marker>(64);
            _decodeCount = 0;
            ResetRenderState();
            output.Failed += OnOutputFailed;
            _output = output;
            _outputRunning = true;
            output.Start();
            PostEvent(() => DeviceChanged?.Invoke(output.DeviceName));
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _output = null;
            _outputRunning = false;
            return false;
        }
    }

    private void ResetRenderState()
    {
        _fade = 0f;
        _gain = _targetGain;
        _ended = true;
        _segTrack = -1;
        Volatile.Write(ref _flushAck, Volatile.Read(ref _flushRequest));
    }

    private void StopOutput()
    {
        var o = _output;
        _outputRunning = false;
        if (o is null) return;
        o.Failed -= OnOutputFailed;
        try { o.Stop(); } catch (Exception e) when (e is not OutOfMemoryException) { }
        o.Dispose();
        _output = null;
    }

    private void OnOutputFailed(string reason)
    {
        _outputRunning = false;
        _wantPlaying = false;
        PostEvent(() => DeviceLost?.Invoke(reason));
    }

    private void ReopenOutputCore()
    {
        var clock = Clock;
        var seg = _current;
        bool playing = _wantPlaying;
        var successor = _successor;

        CloseCurrent();
        StopOutput();
        if (!EnsureOutput())
        {
            _wantPlaying = false;
            PostEvent(() => DeviceLost?.Invoke("没有可用的输出设备"));
            return;
        }

        // 新设备的混音格式可能不同：在新格式下重新打开当前曲目，从记录的位置继续。
        _successor = successor;
        if (seg is not null)
        {
            var position = clock.Track == seg.Track ? clock.Position : TimeSpan.FromSeconds(seg.Reader.PositionFrames / (double)Math.Max(1, clock.SampleRate));
            Execute(new LoadCmd(seg.Track, seg.Path, position, !playing));
        }
    }

    // ───────────────────────── 通知线程 ─────────────────────────

    private void PostEvent(Action a)
    {
        _engineEvents.Enqueue(a);
        _notify.Set();
    }

    private void NotifyLoop()
    {
        while (!_disposed)
        {
            _notify.WaitOne(100);
            while (_renderEvents.TryDequeue(out var e))
            {
                var id = new TrackId(e.Track);
                if (e.Kind == RenderEventKind.Boundary) TrackBoundaryReached?.Invoke(id);
                else TrackEnded?.Invoke(id);
            }
            while (_engineEvents.TryDequeue(out var a)) a();
        }
    }

    // ───────────────────────── 测试钩子 ─────────────────────────

    /// <summary>缓冲区中可读的帧数。</summary>
    internal int BufferedFrames => _ring.Available / _channels;

    /// <summary>解码器已写完所有数据（结束标记已写入或没有曲目）。</summary>
    internal bool DecoderFinished => _decoderFinished && _commands.IsEmpty;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _wake.Set();
        _notify.Set();
        _engineThread.Join(TimeSpan.FromSeconds(2));
        _notifyThread.Join(TimeSpan.FromSeconds(2));
        StopOutput();
        CloseCurrent();
        _wake.Dispose();
        _flushDone.Dispose();
        _notify.Dispose();
    }

    // ───────────────────────── 类型 ─────────────────────────

    private sealed class Segment(TrackId track, string path, ISeekablePcmReader reader)
    {
        public TrackId Track { get; } = track;
        public string Path { get; } = path;
        public ISeekablePcmReader Reader { get; } = reader;
        public bool Ended { get; set; }
    }

    private enum MarkerKind { Start, Boundary, End }

    /// <summary>边界标记：Sample 是全局采样序号（交错 float 的索引）。</summary>
    private readonly record struct Marker(long Sample, MarkerKind Kind, long Track, long OffsetFrames);

    private enum RenderEventKind { Boundary, Ended }

    private readonly record struct RenderEvent(RenderEventKind Kind, long Track);

    private abstract record Command;
    private sealed record LoadCmd(TrackId Track, string Path, TimeSpan StartAt, bool Paused) : Command;
    private sealed record SeekCmd(TimeSpan Position) : Command;
    private sealed record PauseCmd(bool Paused) : Command;
    private sealed record SuccessorCmd((TrackId Id, string Path)? Next) : Command;
    private sealed record StopCmd : Command;
    private sealed record ReopenCmd : Command;
}
