using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Solisten.Core.Model;
using Solisten.Integrations.NetEase.Api;
using Solisten.Integrations.NetEase.Auth;
using Windows.Storage.Streams;

namespace Solisten.App.Integrations;

/// <summary>
/// 扫码登录对话框。打开时生成二维码并开始轮询；关闭、取消或重新生成时停止轮询。
/// 登录成功（账号已验证、凭据已保存）后自动关闭，<see cref="Account"/> 为登录的账号。
/// </summary>
public sealed partial class NetEaseLoginDialog : ContentDialog
{
    private readonly NetEaseQrLoginService _login;
    private readonly DispatcherQueueTimer _countdown;
    private CancellationTokenSource? _cts;
    private Task _waiting = Task.CompletedTask;
    private QrLoginSession? _session;
    private readonly Action<string>? _log;

    public NetEaseLoginDialog(NetEaseQrLoginService login, Action<string>? log = null)
    {
        _login = login;
        _log = log;
        InitializeComponent();
        AdapterNote.Text = login.Provider.IsOfficial ? "" : $"{login.Provider.DisplayName} · 非官方集成";
        _countdown = DispatcherQueue.CreateTimer();
        _countdown.Interval = TimeSpan.FromSeconds(1);
        _countdown.Tick += (_, _) => UpdateCountdown();

        Opened += (_, _) => _waiting = RunAsync();
        Closed += (_, _) => Stop();
        SecondaryButtonClick += (_, args) =>
        {
            args.Cancel = true; // 重新生成时保持对话框打开
            _waiting = RunAsync();
        };
    }

    public RemoteAccount? Account { get; private set; }

    private async Task RunAsync()
    {
        Stop();
        await _waiting; // 等上一轮轮询完全退出，保证同一时间只有一个轮询循环
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;

        IsSecondaryButtonEnabled = false;
        Busy.IsActive = true;
        QrImage.Source = null;
        QrPlaceholder.Visibility = Microsoft.UI.Xaml.Visibility.Collapsed;
        StatusText.Text = "正在生成二维码…";
        CountdownText.Text = "";

        try
        {
            _session = await _login.CreateAsync(ct);
            await ShowQrAsync(_session);
            Busy.IsActive = false;
            UpdateCountdown();
            _countdown.Start();

            var progress = new Progress<QrLoginStatus>(s => { if (!ct.IsCancellationRequested) StatusText.Text = s.Message; });
            var result = await _login.WaitAsync(_session, progress, ct);
            if (ct.IsCancellationRequested) return;

            _countdown.Stop();
            StatusText.Text = result.Message;
            if (result is { State: QrLoginState.Succeeded, Account: { } account })
            {
                Account = account;
                Hide();
                return;
            }
            CountdownText.Text = "";
            IsSecondaryButtonEnabled = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            _log?.Invoke($"netease qr login failed: {Solisten.Integrations.NetEase.Diagnostics.Redactor.Redact(e.ToString())}");
            _countdown.Stop();
            Busy.IsActive = false;
            StatusText.Text = e is NetEaseApiException api ? api.UserMessage : "无法生成二维码";
            CountdownText.Text = "";
            IsSecondaryButtonEnabled = true;
        }
    }

    private async Task ShowQrAsync(QrLoginSession session)
    {
        if (session.QrPng is { Length: > 0 } png)
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(png.AsBuffer());
            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);
            QrImage.Source = bitmap;
            return;
        }
        // Provider 没有提供二维码图片（例如本机演示数据）。
        QrPlaceholder.Text = _login.Provider.IsOfficial ? "无法显示二维码，请重新生成" : "演示数据没有二维码\n稍候会自动完成登录";
        QrPlaceholder.Visibility = Microsoft.UI.Xaml.Visibility.Visible;
    }

    private void UpdateCountdown()
    {
        if (_session is null) return;
        var left = _session.ExpiresAt - DateTimeOffset.UtcNow;
        CountdownText.Text = left > TimeSpan.Zero ? $"{(int)left.TotalMinutes}:{left.Seconds:00} 后过期" : "";
    }

    private void Stop()
    {
        _countdown.Stop();
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
