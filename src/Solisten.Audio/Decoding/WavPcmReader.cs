using System.Buffers.Binary;

namespace Solisten.Audio.Decoding;

/// <summary>
/// 纯托管的 WAV（PCM 16/24/32 位、IEEE float 32 位）读取器。FFmpeg 不可用时作为后备，
/// 也让测试不依赖本机库。重采样使用线性插值，只用于后备场景。
/// </summary>
public sealed class WavPcmReader : ISeekablePcmReader
{
    private readonly Stream _stream;
    private readonly long _dataStart;
    private readonly long _dataLength;
    private readonly int _srcChannels;
    private readonly int _srcRate;
    private readonly int _bytesPerSample;
    private readonly bool _float;
    private readonly long _srcFrames;
    private readonly double _step;

    private byte[] _raw = new byte[16384];
    private float[] _src = [];
    private double _srcPos;
    private long _srcBufStart;
    private int _srcBufFrames;

    public WavPcmReader(string path, int? targetSampleRate = null, int? targetChannels = null)
        : this(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete), targetSampleRate, targetChannels)
    {
    }

    public WavPcmReader(Stream stream, int? targetSampleRate = null, int? targetChannels = null)
    {
        _stream = stream;
        Span<byte> hdr = stackalloc byte[12];
        ReadExactly(hdr);
        if (!hdr[..4].SequenceEqual("RIFF"u8) || !hdr[8..12].SequenceEqual("WAVE"u8)) throw new AudioDecodeException("不是 WAV 文件");

        int format = 0, bits = 0;
        Span<byte> chunk = stackalloc byte[8];
        while (true)
        {
            ReadExactly(chunk);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            if (chunk[..4].SequenceEqual("fmt "u8))
            {
                var fmt = new byte[size];
                ReadExactly(fmt);
                format = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                _srcChannels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2));
                _srcRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14));
                if (format == 0xFFFE && size >= 26) format = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(24));
                if ((size & 1) == 1) _stream.Seek(1, SeekOrigin.Current);
            }
            else if (chunk[..4].SequenceEqual("data"u8))
            {
                _dataStart = _stream.Position;
                _dataLength = Math.Min(size, _stream.Length - _dataStart);
                break;
            }
            else
            {
                _stream.Seek(size + (size & 1), SeekOrigin.Current);
            }
        }

        _float = format == 3;
        if (!(format == 1 && bits is 16 or 24 or 32) && !(_float && bits == 32))
            throw new AudioDecodeException($"不支持的 WAV 格式 {format}/{bits}");
        if (_srcChannels <= 0 || _srcRate <= 0) throw new AudioDecodeException("WAV 头无效");

        _bytesPerSample = bits / 8;
        _srcFrames = _dataLength / (_bytesPerSample * _srcChannels);
        SampleRate = targetSampleRate ?? _srcRate;
        Channels = targetChannels ?? _srcChannels;
        _step = _srcRate / (double)SampleRate;
        Duration = TimeSpan.FromSeconds(_srcFrames / (double)_srcRate);
        _stream.Position = _dataStart;
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public TimeSpan Duration { get; }
    public long PositionFrames { get; private set; }

    public int Read(Span<float> interleaved)
    {
        int frames = interleaved.Length / Channels;
        int written = 0;
        for (int f = 0; f < frames; f++)
        {
            long i0 = (long)Math.Floor(_srcPos);
            if (i0 >= _srcFrames) break;
            double frac = _srcPos - i0;
            if (!EnsureSource(i0, frac > 0 ? i0 + 1 : i0)) break;

            for (int c = 0; c < Channels; c++)
            {
                float a = SourceSample(i0, c);
                float v = frac > 0 && i0 + 1 < _srcFrames ? a + (float)frac * (SourceSample(i0 + 1, c) - a) : a;
                interleaved[written++] = v;
            }
            _srcPos += _step;
            PositionFrames++;
        }
        return written;
    }

    /// <summary>按目标声道数取源声道：单声道输出取平均，单声道源复制到多声道。</summary>
    private float SourceSample(long frame, int outChannel)
    {
        int baseIdx = (int)(frame - _srcBufStart) * _srcChannels;
        if (Channels == _srcChannels) return _src[baseIdx + outChannel];
        if (Channels == 1)
        {
            float sum = 0;
            for (int c = 0; c < _srcChannels; c++) sum += _src[baseIdx + c];
            return sum / _srcChannels;
        }
        return _src[baseIdx + Math.Min(outChannel, _srcChannels - 1)];
    }

    private bool EnsureSource(long first, long last)
    {
        last = Math.Min(last, _srcFrames - 1);
        if (first >= _srcBufStart && last < _srcBufStart + _srcBufFrames) return true;

        int frameBytes = _bytesPerSample * _srcChannels;
        int want = Math.Max(4096, (int)(last - first + 1));
        want = (int)Math.Min(want, _srcFrames - first);
        if (want <= 0) return false;
        if (_raw.Length < want * frameBytes) _raw = new byte[want * frameBytes];
        _stream.Position = _dataStart + first * frameBytes;
        int got = 0;
        while (got < want * frameBytes)
        {
            int n = _stream.Read(_raw, got, want * frameBytes - got);
            if (n <= 0) break;
            got += n;
        }
        int gotFrames = got / frameBytes;
        if (_src.Length < gotFrames * _srcChannels) _src = new float[gotFrames * _srcChannels];
        for (int i = 0; i < gotFrames * _srcChannels; i++)
        {
            var s = _raw.AsSpan(i * _bytesPerSample, _bytesPerSample);
            _src[i] = _bytesPerSample switch
            {
                2 => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                3 => ((s[0] | (s[1] << 8) | (s[2] << 16)) << 8 >> 8) / 8388608f,
                _ when _float => BinaryPrimitives.ReadSingleLittleEndian(s),
                _ => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
            };
        }
        _srcBufStart = first;
        _srcBufFrames = gotFrames;
        return gotFrames > last - first;
    }

    public void Seek(TimeSpan position)
    {
        long frame = Math.Clamp((long)Math.Round(position.TotalSeconds * SampleRate), 0, (long)(_srcFrames / _step));
        PositionFrames = frame;
        _srcPos = frame * _step;
    }

    private void ReadExactly(Span<byte> buf)
    {
        try { _stream.ReadExactly(buf); }
        catch (EndOfStreamException) { throw new AudioDecodeException("WAV 文件被截断"); }
    }

    public void Dispose() => _stream.Dispose();
}
