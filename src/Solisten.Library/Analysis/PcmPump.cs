using Solisten.Core.Audio;

namespace Solisten.Library.Analysis;

/// <summary>接收解码后的交错 PCM。</summary>
public interface IPcmSink
{
    void Add(ReadOnlySpan<float> interleaved);
}

/// <summary>把一次解码同时分发给多个分析器。</summary>
public static class PcmPump
{
    public static void Run(IPcmReader reader, CancellationToken ct, params IPcmSink[] sinks)
    {
        var buffer = new float[4096 * Math.Max(1, reader.Channels)];
        int n;
        while ((n = reader.Read(buffer)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = buffer.AsSpan(0, n);
            foreach (var s in sinks) s.Add(chunk);
        }
    }
}
