using FFmpeg.AutoGen;
using Solisten.Core.Audio;

namespace Solisten.Audio.Decoding;

public sealed class AudioDecodeException(string message) : Exception(message);

/// <summary>可以精确定位的 PCM 读取器。</summary>
public interface ISeekablePcmReader : IPcmReader
{
    /// <summary>定位到指定时间（采样精确：定位到前一个关键帧后解码并丢弃到目标采样）。</summary>
    void Seek(TimeSpan position);

    /// <summary>当前输出位置（输出采样率下的帧数）。</summary>
    long PositionFrames { get; }
}

/// <summary>
/// FFmpeg 解码 + libswresample 转换为 float32 交错采样。
/// 编码器延迟/填充（LAME/Xing、iTunSMPB、skip_samples）由 libavformat/libavcodec 自动裁剪。
/// </summary>
public sealed unsafe class FFmpegPcmReader : ISeekablePcmReader
{
    private readonly string _path;
    private AVFormatContext* _fmt;
    private AVCodecContext* _codec;
    private SwrContext* _swr;
    private AVPacket* _pkt;
    private AVFrame* _frame;
    private int _stream;
    private AVRational _timeBase;

    private float[] _conv = new float[8192];
    private int _pendingOffset;
    private int _pendingCount;

    private long _discardFrames;
    private long _endFrame = long.MaxValue;
    private long _durationTs;
    private TimeSpan? _seekTarget;
    private bool _flushSent;
    private bool _decoderDone;
    private bool _swrDrained;
    private int _swrInRate;
    private AVSampleFormat _swrInFormat;
    private AVChannelLayout _swrInLayout;

    public FFmpegPcmReader(string path, int? targetSampleRate = null, int? targetChannels = null)
    {
        FFmpegLoader.EnsureInitialized();
        _path = path;
        Open();
        SampleRate = targetSampleRate ?? _codec->sample_rate;
        Channels = targetChannels ?? Math.Clamp(_codec->ch_layout.nb_channels, 1, 2);
        UpdateEndFrame();
    }

    private void UpdateEndFrame()
    {
        if (_durationTs > 0) _endFrame = (long)Math.Round(_durationTs * ffmpeg.av_q2d(_timeBase) * SampleRate);
    }

    /// <summary>http / https 地址由 FFmpeg 的网络协议读取（流式，不落盘）。</summary>
    public static bool IsNetworkSource(string path) =>
        path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("http://", StringComparison.OrdinalIgnoreCase);

    public int SampleRate { get; }
    public int Channels { get; }
    public TimeSpan Duration { get; private set; }
    public long PositionFrames { get; private set; }

    private void Open()
    {
        AVFormatContext* fmt = null;
        if (IsNetworkSource(_path))
        {
            // 在线音频（例如网易云的播放地址）：读写超时和断线重连，避免网络抖动时解码线程无限等待。
            AVDictionary* options = null;
            ffmpeg.av_dict_set(&options, "rw_timeout", "15000000", 0); // 微秒
            ffmpeg.av_dict_set(&options, "reconnect", "1", 0);
            ffmpeg.av_dict_set(&options, "reconnect_on_network_error", "1", 0);
            ffmpeg.av_dict_set(&options, "reconnect_delay_max", "4", 0);
            ffmpeg.av_dict_set(&options, "user_agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) Solisten", 0);
            try { Check(ffmpeg.avformat_open_input(&fmt, _path, null, &options), "打开在线音频"); }
            finally { ffmpeg.av_dict_free(&options); }
        }
        else
        {
            if (!File.Exists(_path)) throw new AudioDecodeException("文件不存在");
            Check(ffmpeg.avformat_open_input(&fmt, _path, null, null), "打开文件");
        }
        _fmt = fmt;
        Check(ffmpeg.avformat_find_stream_info(_fmt, null), "读取流信息");

        AVCodec* codec = null;
        _stream = ffmpeg.av_find_best_stream(_fmt, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, &codec, 0);
        if (_stream < 0 || codec is null) throw new AudioDecodeException("文件中没有可解码的音频流");
        for (int i = 0; i < _fmt->nb_streams; i++)
        {
            if (i != _stream) _fmt->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
        }

        var st = _fmt->streams[_stream];
        _timeBase = st->time_base;
        _codec = ffmpeg.avcodec_alloc_context3(codec);
        Check(ffmpeg.avcodec_parameters_to_context(_codec, st->codecpar), "复制解码参数");
        _codec->pkt_timebase = st->time_base;
        Check(ffmpeg.avcodec_open2(_codec, codec, null), "打开解码器");

        _pkt = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();

        if (st->duration > 0 && st->duration != ffmpeg.AV_NOPTS_VALUE)
        {
            Duration = TimeSpan.FromSeconds(st->duration * ffmpeg.av_q2d(st->time_base));
            // MP4/M4A：libavformat 按编辑列表裁掉了起始的编码器延迟，但不裁尾部填充；
            // 流时长是精确的采样数，按它截断输出（§5.4 尾部裁剪）。
            var name = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)_fmt->iformat->name) ?? "";
            if (name.Contains("mp4", StringComparison.Ordinal)) _durationTs = st->duration;
        }
        else if (_fmt->duration > 0)
            Duration = TimeSpan.FromSeconds(_fmt->duration / (double)ffmpeg.AV_TIME_BASE);
    }

    public int Read(Span<float> interleaved)
    {
        int written = 0;
        long remaining = _endFrame - PositionFrames;
        if (remaining <= 0) return 0;
        int want = interleaved.Length - interleaved.Length % Channels;
        if (remaining < want / Channels) want = (int)remaining * Channels;
        while (written < want)
        {
            if (_pendingCount > 0)
            {
                int n = Math.Min(_pendingCount, want - written);
                _conv.AsSpan(_pendingOffset, n).CopyTo(interleaved[written..]);
                _pendingOffset += n;
                _pendingCount -= n;
                written += n;
                PositionFrames += n / Channels;
                continue;
            }
            if (!DecodeMore()) break;
        }
        return written;
    }

    /// <summary>解码下一帧到 _conv；返回 false 表示已经完全结束。</summary>
    private bool DecodeMore()
    {
        while (true)
        {
            if (_decoderDone)
            {
                if (_swrDrained || _swr is null) return false;
                _swrDrained = true;
                int drained = Convert(null, 0);
                if (drained > 0) return true;
                return false;
            }

            int ret = ffmpeg.avcodec_receive_frame(_codec, _frame);
            if (ret == 0)
            {
                int got = ConvertFrame();
                ffmpeg.av_frame_unref(_frame);
                if (got > 0) return true;
                continue;
            }
            if (ret == ffmpeg.AVERROR_EOF)
            {
                _decoderDone = true;
                continue;
            }
            if (ret != ffmpeg.AVERROR(ffmpeg.EAGAIN)) Check(ret, "解码");

            if (_flushSent)
            {
                _decoderDone = true;
                continue;
            }

            ret = ffmpeg.av_read_frame(_fmt, _pkt);
            if (ret == ffmpeg.AVERROR_EOF || (ret < 0 && _fmt->pb is not null && _fmt->pb->eof_reached != 0))
            {
                ffmpeg.avcodec_send_packet(_codec, null);
                _flushSent = true;
                continue;
            }
            Check(ret, "读取数据包");
            if (_pkt->stream_index == _stream)
            {
                ret = ffmpeg.avcodec_send_packet(_codec, _pkt);
                // 损坏的单个数据包不终止播放
                if (ret < 0 && ret != ffmpeg.AVERROR(ffmpeg.EAGAIN) && ret != ffmpeg.AVERROR_INVALIDDATA) Check(ret, "送入数据包");
            }
            ffmpeg.av_packet_unref(_pkt);
        }
    }

    private int ConvertFrame()
    {
        EnsureResampler(_frame);

        if (_seekTarget is { } target)
        {
            _seekTarget = null;
            long pts = _frame->best_effort_timestamp;
            if (pts != ffmpeg.AV_NOPTS_VALUE)
            {
                double frameSec = pts * ffmpeg.av_q2d(_timeBase);
                _discardFrames = Math.Max(0, (long)Math.Round((target.TotalSeconds - frameSec) * SampleRate));
            }
        }

        return Convert(_frame->extended_data, _frame->nb_samples);
    }

    private int Convert(byte** input, int inSamples)
    {
        int outCap = ffmpeg.swr_get_out_samples(_swr, inSamples);
        if (outCap <= 0 && input is not null) return 0;
        outCap = Math.Max(outCap, 0) + 32;
        if (_conv.Length < outCap * Channels) _conv = new float[outCap * Channels * 2];

        int frames;
        fixed (float* p = _conv)
        {
            byte* outPtr = (byte*)p;
            frames = ffmpeg.swr_convert(_swr, &outPtr, outCap, input, inSamples);
        }
        Check(frames, "重采样");

        int offset = 0;
        if (_discardFrames > 0)
        {
            int drop = (int)Math.Min(_discardFrames, frames);
            _discardFrames -= drop;
            offset = drop;
        }
        _pendingOffset = offset * Channels;
        _pendingCount = (frames - offset) * Channels;
        return _pendingCount;
    }

    private void EnsureResampler(AVFrame* frame)
    {
        var inFormat = (AVSampleFormat)frame->format;
        var inRate = frame->sample_rate;
        var inLayout = frame->ch_layout;
        if (_swr is not null && inRate == _swrInRate && inFormat == _swrInFormat && SameLayout(inLayout, _swrInLayout)) return;

        if (_swr is not null)
        {
            var old = _swr;
            ffmpeg.swr_free(&old);
            _swr = null;
        }

        AVChannelLayout outLayout;
        ffmpeg.av_channel_layout_default(&outLayout, Channels);
        SwrContext* swr = null;
        Check(ffmpeg.swr_alloc_set_opts2(&swr, &outLayout, AVSampleFormat.AV_SAMPLE_FMT_FLT, SampleRate,
            &inLayout, inFormat, inRate, 0, null), "创建重采样器");
        Check(ffmpeg.swr_init(swr), "初始化重采样器");
        _swr = swr;
        _swrInRate = inRate;
        _swrInFormat = inFormat;
        _swrInLayout = inLayout;
    }

    private static bool SameLayout(AVChannelLayout a, AVChannelLayout b) => ffmpeg.av_channel_layout_compare(&a, &b) == 0;

    public void Seek(TimeSpan position)
    {
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        _pendingCount = 0;
        _pendingOffset = 0;
        _discardFrames = 0;
        _flushSent = false;
        _decoderDone = false;
        _swrDrained = false;

        if (position == TimeSpan.Zero)
        {
            // 回到开头时重新打开文件，确保编码器延迟裁剪与首次播放完全一致。
            CloseNative();
            Open();
            UpdateEndFrame();
            PositionFrames = 0;
            return;
        }

        long ts = (long)(position.TotalSeconds / ffmpeg.av_q2d(_timeBase));
        Check(ffmpeg.av_seek_frame(_fmt, _stream, ts, ffmpeg.AVSEEK_FLAG_BACKWARD), "定位");
        ffmpeg.avcodec_flush_buffers(_codec);
        if (_swr is not null)
        {
            var old = _swr;
            ffmpeg.swr_free(&old);
            _swr = null;
        }
        _seekTarget = position;
        PositionFrames = (long)Math.Round(position.TotalSeconds * SampleRate);
    }

    private static void Check(int ret, string what)
    {
        if (ret >= 0) return;
        const int size = 256;
        var buf = stackalloc byte[size];
        ffmpeg.av_strerror(ret, buf, size);
        var msg = System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)buf) ?? ret.ToString(System.Globalization.CultureInfo.InvariantCulture);
        throw new AudioDecodeException($"{what}失败：{msg}");
    }

    private void CloseNative()
    {
        if (_swr is not null) { var s = _swr; ffmpeg.swr_free(&s); _swr = null; }
        if (_frame is not null) { var f = _frame; ffmpeg.av_frame_free(&f); _frame = null; }
        if (_pkt is not null) { var p = _pkt; ffmpeg.av_packet_free(&p); _pkt = null; }
        if (_codec is not null) { var c = _codec; ffmpeg.avcodec_free_context(&c); _codec = null; }
        if (_fmt is not null) { var f = _fmt; ffmpeg.avformat_close_input(&f); _fmt = null; }
    }

    public void Dispose()
    {
        CloseNative();
        GC.SuppressFinalize(this);
    }

    ~FFmpegPcmReader() => CloseNative();
}
