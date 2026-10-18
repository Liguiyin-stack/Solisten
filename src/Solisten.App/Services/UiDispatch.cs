using Microsoft.UI.Dispatching;

namespace Solisten.App.Services;

/// <summary>
/// DispatcherQueue 回调中的异常不会进入 Application.UnhandledException，而是以 stowed exception 直接终止进程。
/// 所有排队到 UI 线程的回调都经过这里：记录日志，不让一次界面更新失败带走整个播放器。
/// </summary>
public static class UiDispatch
{
    public static FileLog? Log { get; set; }

    public static bool Run(this DispatcherQueue queue, Action action, DispatcherQueuePriority priority = DispatcherQueuePriority.Normal) =>
        queue.TryEnqueue(priority, () =>
        {
            try { action(); }
            catch (Exception e) { Log?.Write($"ui callback failed: {e}"); }
        });
}
