using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using Solisten.Audio.Engine;

namespace Solisten.Audio.Output;

/// <summary>
/// §5.1/5.2 WASAPI 共享模式、事件驱动输出。所有曲目统一转换为设备混音采样率的 float32 立体声，
/// 因此跨采样率也能无缝播放。渲染线程注册 MMCSS "Pro Audio"。
/// </summary>
public sealed partial class WasapiOutput : IAudioOutput
{
    private const long BufferDuration100ns = 20 * 10_000; // 20 ms
    private const int AudclntEDeviceInvalidated = unchecked((int)0x88890004);

    private readonly MMDevice _device;
    private readonly AudioClient _client;
    private readonly IRenderSource _source;
    private readonly EventWaitHandle _event = new(false, EventResetMode.AutoReset);
    private AudioRenderClient? _render;
    private Thread? _thread;
    private volatile bool _running;
    private int _bufferFrames;

    public WasapiOutput(IRenderSource source, string? deviceId = null)
    {
        _source = source;
        using var enumerator = new MMDeviceEnumerator();
        _device = deviceId is null
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : enumerator.GetDevice(deviceId);
        DeviceName = _device.FriendlyName;
        _client = _device.AudioClient;

        var mix = _client.MixFormat;
        SampleRate = mix.SampleRate;
        Channels = 2;
        var format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
        _client.Initialize(
            AudioClientShareMode.Shared,
            AudioClientStreamFlags.EventCallback | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality,
            BufferDuration100ns, 0, format, Guid.Empty);
        _client.SetEventHandle(_event.SafeWaitHandle.DangerousGetHandle());
        _bufferFrames = _client.BufferSize;
        _render = _client.AudioRenderClient;
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public string DeviceName { get; }

    public event Action<string>? Failed;

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(RenderLoop) { IsBackground = true, Name = "Solisten WASAPI render", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        _event.Set();
        _thread?.Join(TimeSpan.FromSeconds(1));
        _thread = null;
        try { _client.Stop(); } catch (COMException) { }
    }

    private unsafe void RenderLoop()
    {
        int taskIndex = 0;
        var mmcss = AvSetMmThreadCharacteristicsW("Pro Audio", ref taskIndex);
        try
        {
            Fill(_bufferFrames);
            _client.Start();
            while (_running)
            {
                if (!_event.WaitOne(200)) continue;
                if (!_running) break;
                int padding = _client.CurrentPadding;
                int free = _bufferFrames - padding;
                if (free > 0) Fill(free, padding);
            }
        }
        catch (COMException e) when (e.HResult == AudclntEDeviceInvalidated)
        {
            _running = false;
            Failed?.Invoke("输出设备已断开");
        }
        catch (COMException e)
        {
            _running = false;
            Failed?.Invoke(e.Message);
        }
        finally
        {
            if (mmcss != IntPtr.Zero) AvRevertMmThreadCharacteristics(mmcss);
        }
    }

    private unsafe void Fill(int frames, int padding = 0)
    {
        var ptr = _render!.GetBuffer(frames);
        var span = new Span<float>((void*)ptr, frames * Channels);
        _source.Render(span, padding + frames);
        _render.ReleaseBuffer(frames, AudioClientBufferFlags.None);
    }

    public void Dispose()
    {
        Stop();
        _render?.Dispose();
        _render = null;
        _client.Dispose();
        _device.Dispose();
        _event.Dispose();
    }

    [LibraryImport("avrt.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr AvSetMmThreadCharacteristicsW(string taskName, ref int taskIndex);

    [LibraryImport("avrt.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AvRevertMmThreadCharacteristics(IntPtr handle);
}

/// <summary>§5.6 监听默认输出设备变化（IMMNotificationClient）。回调在 COM 线程上，只转发事件。</summary>
public sealed class AudioDeviceWatcher : IMMNotificationClient, IDisposable
{
    private readonly MMDeviceEnumerator _enumerator = new();

    public AudioDeviceWatcher() => _enumerator.RegisterEndpointNotificationCallback(this);

    /// <summary>默认渲染设备变化；参数为新设备 id，没有默认设备时为 null。</summary>
    public event Action<string?>? DefaultDeviceChanged;

    public static IReadOnlyList<(string Id, string Name)> ListDevices()
    {
        using var e = new MMDeviceEnumerator();
        return e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).Select(d => (d.ID, d.FriendlyName)).ToList();
    }

    void IMMNotificationClient.OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
    {
        if (flow == DataFlow.Render && role == Role.Multimedia) DefaultDeviceChanged?.Invoke(string.IsNullOrEmpty(defaultDeviceId) ? null : defaultDeviceId);
    }

    void IMMNotificationClient.OnDeviceStateChanged(string deviceId, DeviceState newState) { }
    void IMMNotificationClient.OnDeviceAdded(string pwstrDeviceId) { }
    void IMMNotificationClient.OnDeviceRemoved(string deviceId) { }
    void IMMNotificationClient.OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }

    public void Dispose()
    {
        _enumerator.UnregisterEndpointNotificationCallback(this);
        _enumerator.Dispose();
    }
}
