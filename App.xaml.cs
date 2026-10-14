using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using SneakyFlow.Services;
using SneakyFlow.UI;

namespace SneakyFlow;

public partial class App : Application
{
    private TorrentService? _service;
    private Mutex? _instance;
    private bool _ownsMutex;
    private bool _closing;
    private bool _closed;
    private bool _starting = true;
    private bool _exitRequested;
    private bool _trayEnabled = true;
    private TrayService? _tray;
    private uint _activationMessage;
    private HwndSource? _windowSource;
    private string _portableRoot = "";
    private ProductStateStore? _product;
    private bool _updating, _removing;
    public UpdateActivity Updates { get; } = new();
    private Task? _updateTask;
    private SettingsWindow? _updateDialog;
    private CancellationTokenSource? _activeUpdateCancellation;
    private CancellationTokenRegistration _dialogCancellation;
    private readonly CancellationTokenSource _background = new();
    private System.Windows.Threading.DispatcherTimer? _updateTimer;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (e.Args.Contains("--apply-update"))
        {
            try { Shutdown(e.Args.Length == 3 && e.Args[0] == "--apply-update" ? await UpdateHandoff.ApplyAsync(e.Args[1], e.Args[2]) : 1); }
            catch { Shutdown(1); }
            return; // Maintenance mode can never start the torrent engine or show the transfer UI.
        }
        var executableDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
        _portableRoot = AppStorage.DefaultProfileDirectory;
        // Keep development / integration checks completely separate from the user's profile.
        var profileIndex = Array.IndexOf(e.Args, "--profile");
        if (profileIndex >= 0 && profileIndex + 1 < e.Args.Length)
            _portableRoot = Path.GetFullPath(e.Args[profileIndex + 1]);

        if (profileIndex < 0)
        {
            try { await Task.Run(() => BrandMigration.Import(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), _portableRoot)); }
            catch (Exception error) { MessageBox.Show(error.Message, "SneakyFlow", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); return; }
        }
        _product = new ProductStateStore(AppStorage.DefaultProfileDirectory);
        if (MaintenanceLease.IsBusy(_product.Root)) { MessageBox.Show(L.T("SneakyFlow завершает обновление или удаление. Повторите запуск позже."), "SneakyFlow"); Shutdown(); return; }
        try
        {
            await _product.RegisterCopyAsync(Environment.ProcessPath!);
            await _product.RegisterRuntimeCacheAsync(typeof(App).Assembly.Location);
        }
        catch (Exception error) { MessageBox.Show(error.Message, "SneakyFlow", MessageBoxButton.OK, MessageBoxImage.Error); Shutdown(1); return; }
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_product.Root.ToUpperInvariant())))[..20];
        _instance = new Mutex(true, @"Local\SneakyFlow-" + key, out _ownsMutex);
        _activationMessage = RegisterWindowMessage("SneakyFlow.Activate." + key);
        if (!_ownsMutex)
        {
            PostMessage(new IntPtr(0xffff), _activationMessage, IntPtr.Zero, IntPtr.Zero);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log(args.Exception);
            MessageBox.Show(args.Exception.Message, L.T("SneakyFlow · ошибка"), MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };
        try
        {
            if (profileIndex < 0) await AppStorage.MigrateLegacyAsync(executableDirectory, _portableRoot);
            _service = new TorrentService(_portableRoot);
            try
            {
                var settingsPath = Path.Combine(_service.DataDirectory, "session.json");
                if (File.Exists(settingsPath))
                {
                    using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(settingsPath));
                    if (saved.RootElement.TryGetProperty("Settings", out var settings))
                    {
                        if (settings.TryGetProperty("Language", out var language) && language.ValueKind == JsonValueKind.String)
                            L.SetLanguage(language.GetString() ?? "ru");
                        if (settings.TryGetProperty("UiScale", out var scale) && scale.ValueKind == JsonValueKind.Number && scale.TryGetDouble(out var factor))
                            UiScale.SetFactor(factor);
                    }
                }
            }
            catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { Log(error); }
            if (!LicensePolicy.IsAccepted(await _product.ReadAsync()))
            {
                var agreement = new AboutWindow(requireAcceptance: true) { ShowInTaskbar = true, WindowStartupLocation = WindowStartupLocation.CenterScreen };
                MainWindow = agreement;
                agreement.SourceInitialized += (_, _) =>
                {
                    _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(agreement).Handle);
                    _windowSource?.AddHook(WindowMessage);
                };
                agreement.ShowDialog();
                _windowSource?.RemoveHook(WindowMessage); _windowSource = null;
                if (agreement.RemovalRequested) { await RemoveApplicationAsync(); return; }
                if (!agreement.Accepted) { Shutdown(); return; }
                await _product.AcceptAsync();
            }
            // Re-read the saved version/hash: do not start transfers if persisting consent failed.
            if (!LicensePolicy.IsAccepted(await _product.ReadAsync())) throw new IOException("License acceptance could not be saved.");
            var window = new MainWindow(_service);
            window.IsEnabled = false;
            MainWindow = window;
            window.Title = L.T("SneakyFlow · Восстановление очереди…");

            window.Closing += WindowClosing;
            window.StateChanged += (_, _) =>
            {
                if (window.WindowState == WindowState.Minimized && _service.Settings.MinimizeToTray && _tray is not null)
                {
                    window.Hide();
                    _tray.NotifyHidden();
                }
            };
            window.SourceInitialized += (_, _) =>
            {
                _windowSource = HwndSource.FromHwnd(new WindowInteropHelper(window).Handle);
                _windowSource?.AddHook(WindowMessage);
            };
            window.Show();
            try { _tray = new TrayService(_service, Dispatcher, ShowMainWindow, RequestExit); }
            catch (Exception error) { _trayEnabled = false; Log(error); }
            await _service.InitializeAsync();
            UiScale.SetFactor(_service.Settings.UiScale);
            window.Language = System.Windows.Markup.XmlLanguage.GetLanguage(L.Culture.IetfLanguageTag);
            _starting = false;
            window.Title = "SneakyFlow";
            window.IsEnabled = true;
            if (e.Args.Length == 3 && e.Args[0] == "--finish-update")
                try
                {
                    var ticket = await UpdateHandoff.FinishAsync(e.Args[1], e.Args[2]);
                    if (ticket.SettingsDraft is not null)
                        _ = Dispatcher.BeginInvoke(() => { _ = window.ShowSettingsAsync(ticket.SettingsDraft, new(UpdateStage.Completed, 100)); });
                }
                catch (Exception error) { Log(error); window.ShowStatus(error.Message, true); }
            if (e.Args.Length == 3 && e.Args[0] == "--update-failed")
                try { Dialogs.ShowMessage(window, L.T("Не удалось обновить SneakyFlow"), await UpdateHandoff.FailureMessageAsync(e.Args[1], e.Args[2])); }
                catch (Exception error) { Log(error); window.ShowStatus(error.Message, true); }
            if (_exitRequested) { RequestExit(); return; }
            for (var i = 0; i < e.Args.Length; i++)
            {
                if (e.Args[i] == "--profile") { i++; continue; }
                if (e.Args[i].StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ||
                    e.Args[i].EndsWith(".torrent", StringComparison.OrdinalIgnoreCase))
                {
                    try { await _service.AddAsync(e.Args[i]); }
                    catch (Exception error) { Log(error); MessageBox.Show(window, error.Message, L.T("Не удалось добавить торрент"), MessageBoxButton.OK, MessageBoxImage.Warning); }
                }
            }
            _updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(6) };
            _updateTimer.Tick += async (_, _) => await CheckForUpdatesAsync(false);
            _updateTimer.Start();
            _ = CheckAfterStartupAsync();
        }
        catch (Exception error)
        {
            Log(error);
            MessageBox.Show(L.T("Не удалось запустить SneakyFlow.\n\n") + error.Message + L.T("\n\nПапка профиля: ") + _portableRoot, "SneakyFlow", MessageBoxButton.OK, MessageBoxImage.Error);
            if (_service is not null) try { await _service.DisposeAsync(); } catch { }
            Shutdown(1);
        }
    }

    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (_closed) return;
        e.Cancel = true;
        if (_starting) return;
        if (!_exitRequested && _trayEnabled && _tray is not null && _service?.Settings.CloseToTray == true)
        {
            if (MainWindow is MainWindow hidden) await hidden.PersistLayoutAsync();
            MainWindow?.Hide();
            _tray.NotifyHidden();
            return;
        }
        if (_closing) return;
        _closing = true;
        _background.Cancel(); _updateTimer?.Stop();
        if (MainWindow is MainWindow window)
        {
            window.BeginShutdown();
            var saved = window.PersistLayoutAsync();
            window.Hide();
            _tray?.Dispose(); _tray = null;
            await saved;
        }
        try { if (_service is not null) await _service.DisposeAsync(); }
        catch (Exception error) { Log(error); }
        finally
        {
            _closed = true;
            _tray?.Dispose();
            _tray = null;
            MainWindow?.Close();
            Shutdown();
        }
    }

    private void ShowMainWindow()
    {
        if (_closing || _removing || MainWindow is null) return;
        if (MainWindow is MainWindow window) { window.RestoreFromTray(); return; }
        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    private void RequestExit()
    {
        _exitRequested = true;
        MainWindow?.Close();
    }

    private async Task CheckAfterStartupAsync()
    {
        try { await Task.Delay(TimeSpan.FromSeconds(30), _background.Token); await CheckForUpdatesAsync(false); }
        catch (OperationCanceledException) { }
    }

    public void AttachUpdateDialog(SettingsWindow dialog)
    {
        if (!_updating) return;
        _updateDialog = dialog;
        _dialogCancellation.Dispose();
        if (_activeUpdateCancellation is { } cancellation)
            _dialogCancellation = dialog.UpdateCancellation.Register(cancellation.Cancel);
    }

    public Task CheckForUpdatesAsync(bool manual, SettingsWindow? dialog = null)
    {
        if (_closing || _starting || _removing || _service is null)
        {
            if (manual) dialog?.ShowUpdateProgress(new(UpdateStage.Failed, Detail: L.T("Проверка обновлений недоступна.")));
            return Task.CompletedTask;
        }
        if (!manual && (!_service.Settings.AutomaticUpdates || Windows.OfType<Window>().Any(w => w != MainWindow && w.IsVisible))) return Task.CompletedTask;
        if (_updating)
        {
            if (dialog is not null) { AttachUpdateDialog(dialog); dialog.ShowUpdateProgress(Updates.Current); }
            return _updateTask ?? Task.CompletedTask;
        }
        _updateDialog = dialog;
        return _updateTask = RunUpdateAsync(manual);
    }

    private async Task RunUpdateAsync(bool manual)
    {
        _updating = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_background.Token);
        _activeUpdateCancellation = cancellation;
        if (_updateDialog is { } owner) AttachUpdateDialog(owner);
        var token = cancellation.Token;
        var window = MainWindow as MainWindow;
        string? directory = null;
        bool handedOff = false;
        try
        {
            Updates.Report(new(UpdateStage.Checking));
            using var http = ReleaseClient.CreateClient();
            var releases = new ReleaseClient(http);
            using var checkTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            checkTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            var release = await releases.CheckAsync(AppVersion.Number, checkTimeout.Token, AppVersion.IsPrerelease);
            if (release is null) { Updates.Report(new(UpdateStage.UpToDate)); return; }
            var productState = await _product!.ReadAsync();
            if (!manual && productState.FailedUpdateVersion == release.Version.ToString() &&
                productState.FailedUpdateAt is { } failedAt && DateTimeOffset.UtcNow - failedAt < TimeSpan.FromDays(1))
            { Updates.Report(new(UpdateStage.Idle)); return; }
            directory = UpdateHandoff.CreateDirectory();
            window?.ShowStatus(L.T("Загружаем и проверяем обновление…"));
            await releases.DownloadAsync(release, Path.Combine(directory, "SneakyFlow.exe"), token, Updates);
            // The initiating Settings window stays open; unrelated dialogs still block restart.
            while (Windows.OfType<Window>().Any(w => w != MainWindow && w != _updateDialog && w.IsVisible))
            {
                Updates.Report(new(UpdateStage.Waiting, Detail: L.T("Закройте другие диалоги, чтобы продолжить установку.")));
                await Task.Delay(250, token);
            }
            token.ThrowIfCancellationRequested();
            if (_closing || _removing) return;
            if (window is not null) window.IsEnabled = false;
            await UpdateHandoff.StartAsync(directory, release, Environment.ProcessPath!, token, Updates, _updateDialog?.CaptureDraft());
            handedOff = true;
            window?.ShowStatus(L.T("Обновление проверено. Сохраняем очередь и перезапускаем SneakyFlow…"));
            _updateDialog?.AllowUpdateRestart();
            _updateDialog?.Close();
            RequestExit();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { Updates.Report(new(UpdateStage.Cancelled)); }
        catch (Exception error)
        {
            Log(error);
            Updates.Report(new(UpdateStage.Failed, Detail: L.T("Не удалось обновить SneakyFlow") + ": " + error.Message));
            if (_updateDialog is null) window?.ShowStatus(L.T("Не удалось обновить SneakyFlow") + ": " + error.Message, true);
        }
        finally
        {
            _updating = false;
            _dialogCancellation.Dispose(); _activeUpdateCancellation = null; _updateDialog = null;
            if (!handedOff && !_removing && !_closing && window is not null) window.IsEnabled = true;
            if (!handedOff && directory is not null && !File.Exists(Path.Combine(directory, "ready")))
            {
                // Do not delete an executing helper or an operation it has accepted.
                try
                {
                    foreach (var file in new[] { "SneakyFlow.exe", "work.json", "cancel" }) File.Delete(Path.Combine(directory, file));
                    Directory.Delete(directory, false);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Log(error); }
            }
        }
    }

    public async Task RemoveApplicationAsync()
    {
        if (_removing || _closing) return;
        _removing = true;
        try
        {
            if (MainWindow is MainWindow window) { window.BeginShutdown(); window.Hide(); }
            _background.Cancel(); _updateTimer?.Stop();
            if (_product is null) throw new InvalidOperationException("Profile is not ready.");
            await _product.RevokeAsync();
            await SelfRemoval.PrepareAsync(_product);
        }
        catch (Exception error)
        {
            Log(error);
            MessageBox.Show(error.Message, L.T("Не удалось завершить самоудаление"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            // Revocation always stops transfers, even if file permissions prevented removal.
            if (_starting) { if (_service is not null) await _service.DisposeAsync(); Shutdown(); }
            else RequestExit();
        }
    }

    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)message == _activationMessage) { ShowMainWindow(); handled = true; }
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

    private void Log(Exception error)
    {
        try
        {
            var dir = Path.Combine(_portableRoot, "SneakyFlow.Data");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "errors.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1_048_576)
                File.Move(path, path + ".previous", true);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O}\n{error}\n\n");
        }
        catch { Debug.WriteLine(error); }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _windowSource?.RemoveHook(WindowMessage);
        if (_ownsMutex) _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
