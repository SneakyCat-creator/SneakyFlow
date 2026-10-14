using System.Drawing;
using System.Reflection;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace SneakyFlow.Services;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Icon _ownedIcon;
    private readonly NativeTrayMenu _menu;
    private readonly DispatcherTimer _timer;
    private readonly TorrentService _service;
    private bool _notified;

    public TrayService(TorrentService service, Dispatcher dispatcher, Action show, Action exit)
    {
        _service = service;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("SneakyFlow.Assets.SneakyFlow.ico")
            ?? throw new InvalidOperationException(L.T("Не найден встроенный значок программы."));
        _ownedIcon = new Icon(stream);
        _menu = new NativeTrayMenu();
        _icon = new Forms.NotifyIcon { Icon = _ownedIcon, Text = "SneakyFlow", Visible = true };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) dispatcher.BeginInvoke(show);
            else if (e.Button == Forms.MouseButtons.Right)
            {
                var command = _menu.Show();
                if (command == NativeTrayMenu.Open) dispatcher.BeginInvoke(show);
                else if (command == NativeTrayMenu.Exit) dispatcher.BeginInvoke(exit);
            }
        };
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Update(), dispatcher);
        _timer.Start();
        Update();
    }

    private void Update()
    {
        var active = _service.Torrents.Count(x => !x.IsPaused && !x.IsComplete);
        _icon.Text = L.F($"SneakyFlow · загрузок: {active}\n↓ {Speed(_service.DownloadSpeed)}   ↑ {Speed(_service.UploadSpeed)}");
    }

    public void NotifyHidden()
    {
        if (_notified) return;
        _notified = true;
        _icon.ShowBalloonTip(3500, L.T("SneakyFlow работает в трее"), L.T("Загрузки продолжаются. Нажмите значок, чтобы открыть окно; «Выход» в его меню завершит программу."), Forms.ToolTipIcon.Info);
    }

    private static string Speed(long bytes) => UI.DisplayFormat.Speed(bytes);

    public void Dispose()
    {
        _timer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _ownedIcon.Dispose();
    }

}
