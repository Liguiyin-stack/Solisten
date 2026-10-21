using System.Diagnostics;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Solisten.App.Services;
using Solisten.Audio.Decoding;
using Solisten.Core.Model;
using Solisten.Integrations.NetEase.Sync;
using Solisten.Library.Remote;
using FocusState = Microsoft.UI.Xaml.FocusState;

namespace Solisten.App;

/// <summary>曲库文件夹一行。</summary>
public sealed record RootRow(long Id, string Path, string Status);

/// <summary>网易云歌单一行。自建歌单总会同步（不能取消勾选）。</summary>
public sealed record NetEasePlaylistRow(string Id, string Name, string Detail, bool Selected, bool CanToggle, bool CanBrowse, bool Owned)
{
    public string BrowseName => $"浏览 {Name}";
    public string SyncName => $"同步 {Name}";
    public string SyncTip => Owned ? "自建歌单总会同步" : "勾选后在下次同步时一起同步";
}

/// <summary>
/// §8.7 设置：附着在画布右侧的薄片，不是永久侧栏。每一项修改立即生效并保存到 settings.json。
/// </summary>
public sealed partial class SettingsPanel : UserControl
{
    private AppHost? _host;
    private Func<Task<string?>>? _pickFolder;
    private bool _loading;
    private List<string?> _deviceIds = [];
    private int _scans;

    public SettingsPanel()
    {
        InitializeComponent();
    }

    /// <summary>用户请求关闭。</summary>
    public event Action? CloseRequested;

    /// <summary>影响画布外观的设置变化（聆听空间模式、减少动态效果）。</summary>
    public event Action? CanvasSettingsChanged;

    public void Attach(AppHost host, Func<Task<string?>> pickFolder)
    {
        _host = host;
        _pickFolder = pickFolder;
        host.Library.RootsChanged += () => DispatcherQueue.Run(RefreshLibrary);
        host.Library.ScanProgressChanged += p => DispatcherQueue.Run(() =>
        {
            if (_scans > 0) ScanStatus.Text = $"正在读取 {p.Processed} / {p.Total}";
        });
        if (host.NetEase is { } netease) netease.Changed += () => DispatcherQueue.Run(RefreshNetEase);
    }

    /// <summary>薄片的底色跟随画布的明暗（由主窗口设置）。</summary>
    public Brush SheetBackground
    {
        get => Sheet.Background;
        set => Sheet.Background = value;
    }

    /// <summary>每次打开时刷新：设备列表、曲库状态可能已经变化。</summary>
    public void Refresh()
    {
        if (_host is not { } host) return;
        _loading = true;
        try
        {
            var s = host.Settings;
            EndBehaviorChoice.SelectedIndex = s.EndBehavior == EndBehavior.Stop ? 0 : 1;
            SpaceChoice.SelectedIndex = s.Space switch
            {
                Rendering.Space.SpaceMode.Quiet => 0,
                Rendering.Space.SpaceMode.Immersive => 2,
                _ => 1,
            };
            MotionChoice.SelectedIndex = (int)s.ReduceMotion;
            RefreshDevices(s.OutputDeviceId);
            RefreshLibrary();
            RefreshNetEase();
            RefreshAbout();
        }
        finally { _loading = false; }
    }

    /// <summary>打开时从顶部开始，焦点放在关闭按钮上（Esc 也可关闭）。</summary>
    public void FocusFirst()
    {
        Scroller.ChangeView(null, 0, null, disableAnimation: true);
        CloseButton.Focus(FocusState.Programmatic);
    }

    // ───────────────────────── 曲库 ─────────────────────────

    private void RefreshLibrary()
    {
        if (_host is not { } host) return;
        var index = host.Library.Index;
        var counts = index.Tracks.Values
            .GroupBy(t => RootIdOf(t))
            .ToDictionary(g => g.Key, g => (Total: g.Count(), Available: g.Count(t => t.Available)));

        var rows = host.Library.Roots.Select(r =>
        {
            counts.TryGetValue(r.Id, out var c);
            string status = !Directory.Exists(r.Path) ? "离线：文件夹不可访问，重新连接后会自动恢复"
                : c.Total == 0 ? "没有可导入的歌"
                : c.Available == c.Total ? $"{c.Total} 首"
                : $"{c.Available} / {c.Total} 首可用";
            return new RootRow(r.Id, r.Path, status);
        }).ToList();

        RootList.ItemsSource = rows;
        NoRoots.Visibility = Ui.Visible(rows.Count == 0);
        RescanAllButton.IsEnabled = rows.Count > 0 && _scans == 0;

        var unsupported = host.Library.Unsupported;
        UnsupportedSummary.Text = unsupported.Count == 0
            ? ""
            : $"{unsupported.Count} 个文件格式暂不支持（APE、WavPack、DSD 等）";
        UnsupportedExpander.Visibility = Ui.Visible(unsupported.Count > 0);
        UnsupportedList.ItemsSource = unsupported;
    }

    /// <summary>GroupKeys.Folder 的形式是 "{rootId}:{相对文件夹}"。</summary>
    private static long RootIdOf(Track t)
    {
        var key = t.Groups.Folder;
        int colon = key.IndexOf(':');
        return colon > 0 && long.TryParse(key.AsSpan(0, colon), out var id) ? id : -1;
    }

    private async Task RunScan(Func<Task> scan, string done)
    {
        _scans++;
        RescanAllButton.IsEnabled = false;
        ScanStatus.Text = "正在扫描…";
        try
        {
            await scan();
            ScanStatus.Text = done;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ScanStatus.Text = $"扫描失败：{e.Message}";
        }
        finally
        {
            _scans--;
            RefreshLibrary();
        }
    }

    private async void OnAddRootClick(object sender, RoutedEventArgs e)
    {
        if (_host is not { } host || _pickFolder is null) return;
        var path = await _pickFolder();
        if (path is null) return;
        await RunScan(() => host.AddFolderAsync(path), "导入完成");
    }

    private async void OnRescanAllClick(object sender, RoutedEventArgs e)
    {
        if (_host is not { } host) return;
        await RunScan(() => host.Library.RescanAsync(), "扫描完成");
    }

    private async void OnRescanRootClick(object sender, RoutedEventArgs e)
    {
        if (_host is not { } host || (sender as FrameworkElement)?.Tag is not long id) return;
        await RunScan(() => host.Library.RescanAsync(id), "扫描完成");
    }

    private async void OnRemoveRootClick(object sender, RoutedEventArgs e)
    {
        if (_host is not { } host || (sender as FrameworkElement)?.Tag is not long id) return;
        var root = host.Library.Roots.FirstOrDefault(r => r.Id == id);
        if (root is null) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = "从曲库移除这个文件夹？",
            Content = $"{root.Path}\n\n文件夹里的音乐文件不会被删除。但 Solisten 为这些歌记录的播放次数、收藏、标签、备注和时间标记会一起移除。",
            PrimaryButtonText = "移除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            await host.Library.RemoveRootAsync(id);
            ScanStatus.Text = "已移除";
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            host.Log.Write($"remove root failed: {ex}");
            ScanStatus.Text = $"移除失败：{ex.Message}";
        }
        RefreshLibrary();
    }

    // ───────────────────────── 播放 / 输出 / 画布 ─────────────────────────

    private void OnEndBehaviorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _host is null || EndBehaviorChoice.SelectedIndex < 0) return;
        _host.SetEndBehavior(EndBehaviorChoice.SelectedIndex == 0 ? EndBehavior.Stop : EndBehavior.ContinueNextGroup);
    }

    private void RefreshDevices(string? selected)
    {
        var devices = AppHost.ListOutputDevices();
        _deviceIds = [null, .. devices.Select(d => (string?)d.Id)];
        var names = new List<string> { "跟随系统默认设备" };
        names.AddRange(devices.Select(d => d.Name));

        int index = _deviceIds.IndexOf(selected);
        if (index < 0)
        {
            // 保存的设备当前不可用（例如耳机已断开）：保留选择，不悄悄改回默认设备。
            _deviceIds.Add(selected);
            names.Add("已保存的设备（当前不可用）");
            index = names.Count - 1;
        }
        DeviceChoice.ItemsSource = names;
        DeviceChoice.SelectedIndex = index;
    }

    private void OnDeviceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _host is null || DeviceChoice.SelectedIndex < 0 || DeviceChoice.SelectedIndex >= _deviceIds.Count) return;
        _host.SetOutputDevice(_deviceIds[DeviceChoice.SelectedIndex]);
    }

    private void OnSpaceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _host is null || SpaceChoice.SelectedIndex < 0) return;
        var mode = SpaceChoice.SelectedIndex switch
        {
            0 => Rendering.Space.SpaceMode.Quiet,
            2 => Rendering.Space.SpaceMode.Immersive,
            _ => Rendering.Space.SpaceMode.Standard,
        };
        _host.UpdateSettings(s => s with { Space = mode });
        CanvasSettingsChanged?.Invoke();
    }

    private void OnMotionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _host is null || MotionChoice.SelectedIndex < 0) return;
        _host.UpdateSettings(s => s with { ReduceMotion = (ReduceMotionMode)MotionChoice.SelectedIndex });
        CanvasSettingsChanged?.Invoke();
    }

    // ───────────────────────── 服务集成：网易云音乐 ─────────────────────────

    private void RefreshNetEase()
    {
        var vm = _host?.NetEase;
        NetEaseUnavailable.Visibility = Ui.Visible(vm is null);
        if (vm is null)
        {
            NetEaseBadge.Visibility = Visibility.Collapsed;
            NetEaseSubtitle.Text = "暂不可用";
            NetEaseSignedOut.Visibility = NetEaseSignedIn.Visibility = NetEasePlaylists.Visibility = Visibility.Collapsed;
            NetEaseStatus.Text = "";
            return;
        }

        bool connected = vm.IsConnected;
        bool syncing = vm.IsSyncing;
        var account = vm.Account;

        // 账号：已登录时显示昵称和首字，未登录时显示服务名。
        var nickname = connected ? account?.Account.Nickname : null;
        NetEaseAccount.Text = connected ? nickname ?? "正在确认账号…" : "网易云音乐";
        NetEaseAvatarText.Text = string.IsNullOrEmpty(nickname) ? "云" : char.IsSurrogate(nickname[0]) ? nickname[..2] : nickname[..1].ToUpperInvariant();
        NetEaseSubtitle.Text = connected ? (vm.Provider.IsOfficial ? "网易云音乐" : "网易云音乐 · 非官方集成") : "未登录";
        NetEaseBadge.Visibility = Ui.Visible(connected);
        if (connected && account is null) vm.EnsureAccount();

        NetEaseSignedOut.Visibility = Ui.Visible(!connected);
        NetEaseConsent.Visibility = Ui.Visible(!vm.Provider.IsOfficial);
        NetEaseConsent.IsChecked = !vm.RequiresConsent;
        NetEaseLoginButton.IsEnabled = !vm.RequiresConsent;

        NetEaseSignedIn.Visibility = Ui.Visible(connected);
        NetEaseSyncButton.IsEnabled = !syncing;
        NetEaseCancelButton.Visibility = Ui.Visible(syncing);
        NetEaseLogoutButton.IsEnabled = !syncing;
        bool loading = _loading;
        _loading = true;
        NetEaseQualityChoice.SelectedIndex = (int)_host!.Settings.NetEaseQuality;
        _loading = loading;

        NetEaseStatus.Text = vm.Progress is { } p ? FormatProgress(p)
            : FormatLastRun(vm.LastRun, account?.LastSyncAt, connected);

        var playlists = vm.Playlists.Where(pl => !pl.RemoteDeleted).Select(pl => new NetEasePlaylistRow(
            pl.Playlist.Id,
            pl.Playlist.Name,
            PlaylistDetail(pl, connected),
            pl.Playlist.IsOwned || vm.IsSelected(pl.Playlist.Id),
            !pl.Playlist.IsOwned && !syncing,
            pl.Matched > 0 || (connected && pl.CachedTracks > 0),
            pl.Playlist.IsOwned)).ToList();
        NetEasePlaylistList.ItemsSource = playlists;
        NetEasePlaylists.Visibility = Ui.Visible(playlists.Count > 0);
    }

    private static string PlaylistDetail(StoredPlaylist pl, bool connected)
    {
        var kind = pl.Playlist.IsOwned ? "自建" : "收藏";
        if (pl.CachedTracks == 0) return $"{kind} · {pl.Playlist.TrackCount} 首 · 未同步";
        return connected
            ? $"{kind} · {pl.CachedTracks} 首 · {pl.Matched} 首本地文件"
            : $"{kind} · 本地可播放 {pl.Matched} / {pl.CachedTracks} 首";
    }

    private static string FormatProgress(SyncProgress p) => p.Stage switch
    {
        SyncStage.Validating => "正在确认登录…",
        SyncStage.FetchingPlaylists => "正在读取歌单…",
        SyncStage.FetchingTracks => $"同步中 · 歌单 {p.PlaylistsDone}/{p.PlaylistsTotal} · 歌曲 {p.TracksSeen}/{p.TracksTotal}",
        SyncStage.Matching => "正在匹配本地文件…",
        _ => "同步完成",
    };

    private static string FormatLastRun(SyncRun? run, DateTimeOffset? lastSync, bool connected)
    {
        if (run is null) return connected ? "还没有同步过" : "";
        var when = lastSync is { } at ? FormatWhen(at) : "";
        return run.Status switch
        {
            SyncRunStatus.Succeeded => $"{when} 已同步 · {run.PlaylistsSeen} 个歌单 · {run.TracksSeen} 首",
            SyncRunStatus.PartiallySucceeded => $"{when} 部分同步 · {run.ErrorMessage}",
            SyncRunStatus.Cancelled => "上次同步已取消",
            SyncRunStatus.Running => "正在同步…",
            _ => $"同步失败：{run.ErrorMessage}",
        };
    }

    private static string FormatWhen(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        return local.Date == DateTime.Today ? $"今天 {local:HH:mm}" : $"{local:yyyy-MM-dd HH:mm}";
    }

    private void OnNetEaseConsentClick(object sender, RoutedEventArgs e)
    {
        if (_loading || _host?.NetEase is not { } vm) return;
        vm.AcceptUnofficial(NetEaseConsent.IsChecked == true);
        RefreshNetEase();
    }

    private async void OnNetEaseLoginClick(object sender, RoutedEventArgs e)
    {
        if (_host?.NetEase is not { } vm || vm.RequiresConsent) return;
        var dialog = new Integrations.NetEaseLoginDialog(vm.Login, _host!.Log.Write) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        await dialog.ShowAsync();
        if (dialog.Account is not { } account) return;
        vm.OnSignedIn(account);

        var ask = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = $"已登录：{account.Nickname}",
            Content = "是否立即同步歌单？默认只同步你创建的歌单，收藏歌单可以稍后勾选。",
            PrimaryButtonText = "立即同步",
            CloseButtonText = "稍后",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await ask.ShowAsync() == ContentDialogResult.Primary) await vm.SyncNowAsync();
    }

    private async void OnNetEaseSyncClick(object sender, RoutedEventArgs e)
    {
        if (_host?.NetEase is { } vm) await vm.SyncNowAsync();
    }

    private void OnNetEaseCancelClick(object sender, RoutedEventArgs e) => _host?.NetEase?.CancelSync();

    private async void OnNetEaseLogoutClick(object sender, RoutedEventArgs e)
    {
        if (_host?.NetEase is not { } vm) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = "退出网易云音乐？",
            Content = "本机保存的登录信息会被删除。已同步的歌单可以保留（以后重新登录时继续使用），也可以一起删除。本地音乐文件和曲库不受影响。",
            PrimaryButtonText = "退出，保留歌单",
            SecondaryButtonText = "退出并删除歌单",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.None) return;
        await vm.LogoutAsync(deleteCache: result == ContentDialogResult.Secondary);
    }

    private void OnNetEaseQualityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _host?.NetEase is not { } vm || NetEaseQualityChoice.SelectedIndex < 0) return;
        vm.SetQuality((Solisten.Integrations.NetEase.Api.StreamQuality)NetEaseQualityChoice.SelectedIndex);
    }

    private void OnNetEaseBrowseClick(object sender, RoutedEventArgs e)
    {
        if (_host is not { } host || (sender as FrameworkElement)?.Tag is not string id) return;
        if (host.ShowRemotePlaylist(id) > 0) CloseRequested?.Invoke();
        else NetEaseStatus.Text = "这个歌单里没有可以播放的歌曲：登录后可以在线播放，或者把音乐文件加入曲库。";
    }

    private void OnNetEasePlaylistClick(object sender, RoutedEventArgs e)
    {
        if (_host?.NetEase is not { } vm || sender is not CheckBox { Tag: string id } box) return;
        vm.SetSelected(id, box.IsChecked == true);
    }

    // ───────────────────────── 关于 ─────────────────────────

    private void RefreshAbout()
    {
        if (_host is not { } host) return;
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
        int plus = version.IndexOf('+');
        if (plus > 0) version = version[..plus];
        // FFmpeg 的版本串很长（n8.1.3-18-g…-日期）：只显示发行版本号。
        var ffmpeg = FFmpegLoader.IsAvailable ? $"FFmpeg {FFmpegLoader.Version?.Split('-')[0]}" : "FFmpeg 未找到（只能播放 WAV）";
        AboutVersion.Text = $"Solisten {version}";
        AboutData.Text = $"{ffmpeg} · 数据保存在 {host.Paths.Root}";
    }

    private void OnLicensesClick(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "licenses");
        if (Directory.Exists(dir)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = false });
    }

    private void OnDataFolderClick(object sender, RoutedEventArgs e)
    {
        if (_host is { } host) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{host.Paths.Root}\"") { UseShellExecute = false });
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
}
