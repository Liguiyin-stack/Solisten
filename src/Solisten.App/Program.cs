using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Solisten.Library.Metadata;
using Windows.ApplicationModel.Activation;

namespace Solisten.App;

/// <summary>
/// §8.6 单实例：第二个进程把自己的命令行（文件或文件夹路径）转发给已经运行的实例后退出。
/// 第一个实例在 <see cref="PathsReceived"/> 中收到路径，由界面决定如何展示，不自动打断当前歌曲。
/// </summary>
public static partial class Program
{
    private const string InstanceKey = "Solisten.Main";
    private static readonly ConcurrentQueue<IReadOnlyList<string>> Pending = new();
    private static Action<IReadOnlyList<string>>? _receiver;

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (RedirectToRunningInstance()) return 0;

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    /// <summary>可以打开的路径：文件夹或支持的音频文件（过滤掉 exe 自身和其他参数）。</summary>
    public static IReadOnlyList<string> OpenablePaths(IEnumerable<string> args) =>
        [.. args.Where(a => !string.IsNullOrWhiteSpace(a))
                .Where(a => Directory.Exists(a) || (File.Exists(a) && TrackMetadataReader.IsSupported(a)))];

    /// <summary>
    /// 界面准备好后注册接收器；在此之前收到的路径会先排队。
    /// 接收器在后台线程上被调用。
    /// </summary>
    public static void OnPathsReceived(Action<IReadOnlyList<string>> receiver)
    {
        _receiver = receiver;
        while (Pending.TryDequeue(out var paths)) receiver(paths);
    }

    private static bool RedirectToRunningInstance()
    {
        AppInstance key;
        try { key = AppInstance.FindOrRegisterForKey(InstanceKey); }
        catch (COMException) { return false; } // 单实例不可用时按普通多实例运行

        if (key.IsCurrent)
        {
            key.Activated += OnActivated;
            return false;
        }

        var args = AppInstance.GetCurrent().GetActivatedEventArgs();
        // 允许已运行的实例把窗口带到前台。
        AllowSetForegroundWindow(key.ProcessId);

        // RedirectActivationToAsync 不能在 STA 线程上同步等待：在线程池上执行，用事件等待完成。
        using var done = new ManualResetEventSlim();
        Task.Run(async () =>
        {
            try { await key.RedirectActivationToAsync(args); }
            finally { done.Set(); }
        });
        done.Wait(TimeSpan.FromSeconds(10));
        return true;
    }

    private static void OnActivated(object? sender, AppActivationArguments e)
    {
        var paths = e.Kind switch
        {
            ExtendedActivationKind.Launch when e.Data is ILaunchActivatedEventArgs l => OpenablePaths(SplitCommandLine(l.Arguments)),
            ExtendedActivationKind.File when e.Data is IFileActivatedEventArgs f => OpenablePaths(f.Files.Select(i => i.Path)),
            _ => [],
        };

        // 没有路径（例如再次双击程序图标）时也要把窗口带到前台。
        if (_receiver is { } r) r(paths);
        else Pending.Enqueue(paths);
    }

    internal static IEnumerable<string> SplitCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return [];
        var argv = CommandLineToArgvW(commandLine, out int count);
        if (argv == IntPtr.Zero) return [];
        try
        {
            var result = new string[count];
            for (int i = 0; i < count; i++) result[i] = Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size)) ?? "";
            return result;
        }
        finally { LocalFree(argv); }
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CommandLineToArgvW(string cmdLine, out int numArgs);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr mem);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(uint processId);
}
