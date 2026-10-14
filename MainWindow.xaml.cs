using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SneakyFlow.Models;
using SneakyFlow.Services;
using SneakyFlow.UI;

namespace SneakyFlow;

public partial class MainWindow : Window
{
    private readonly TorrentService _service;
    private readonly ICollectionView _view;
    private readonly DispatcherTimer _refreshTimer;
    private bool _busy, _shuttingDown;
    private readonly WindowLayoutStore _layoutStore;
    private readonly DispatcherTimer _layoutTimer;
    private bool _restoringLayout = true;
    private WindowState _lastVisibleState = WindowState.Normal;
    private bool _detailsVisible, _layoutLoaded;
    private double _preferredDetailsHeight = 340;
    private DateTime _noticeUntil;
    private TorrentItem? SelectedTorrent => TorrentList.SelectedItem as TorrentItem;

    public MainWindow(TorrentService service)
    {
        _service = service;
        _layoutStore = new WindowLayoutStore(service.DataDirectory);
        InitializeComponent();
        _view = CollectionViewSource.GetDefaultView(service.Torrents);

        TorrentList.ItemsSource = _view;
        PortableLabel.ToolTip = L.T("Настройки, список раздач и журнал: ") + service.DataDirectory;
        _service.Warning += Service_Warning;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refreshTimer.Tick += (_, _) => RefreshUi();
        _refreshTimer.Start();
        PreviewKeyDown += Window_PreviewKeyDown;
        _layoutTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _layoutTimer.Tick += async (_, _) => { _layoutTimer.Stop(); await PersistLayoutAsync(); };
        Loaded += (_, _) => RestoreLayout();
        SizeChanged += (_, _) => ScheduleLayoutSave();
        LocationChanged += (_, _) => ScheduleLayoutSave();
        StateChanged += (_, _) => { if (WindowState != WindowState.Minimized) _lastVisibleState = WindowState; ScheduleLayoutSave(); };
        RefreshUi();
    }

    private void RestoreLayout()
    {
        var saved = _layoutStore.Load();
        if (saved is not null)
        {
            Width = Math.Max(MinWidth, saved.Width);
            Height = Math.Max(MinHeight, saved.Height);
            if (saved.Left is { } left && saved.Top is { } top)
            {
                var visible = System.Windows.Forms.Screen.AllScreens.Any(screen =>
                {
                    var matrix = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
                    var area = screen.WorkingArea;
                    var bounds = new MatrixTransform(matrix).TransformBounds(new Rect(area.X, area.Y, area.Width, area.Height));
                    return new Rect(left, top, Width, 48).IntersectsWith(bounds);
                });
                if (visible) { Left = left; Top = top; }
            }
            _preferredDetailsHeight = Math.Clamp(saved.DetailsHeight, 240, 3000);
            _layoutLoaded = true;
            if (saved.Maximized) WindowState = WindowState.Maximized;
        }
        _restoringLayout = false;
        ApplyDetailsHeight();
    }

    private void ScheduleLayoutSave()
    {
        if (_restoringLayout || _shuttingDown || !IsLoaded) return;
        _layoutTimer.Stop(); _layoutTimer.Start();
    }

    public async Task PersistLayoutAsync()
    {
        if (_restoringLayout) return;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (bounds.IsEmpty || bounds.Width <= 0) return;
        try { await _layoutStore.SaveAsync(new(bounds.Width, bounds.Height, bounds.Left, bounds.Top,
            _lastVisibleState == WindowState.Maximized, _preferredDetailsHeight)); }
        catch (Exception ex) { Notify(L.T("Не удалось сохранить размер окна: ") + ex.Message, true); }
    }

    public void BeginShutdown()
    {
        _shuttingDown = true;
        _refreshTimer.Stop(); _layoutTimer.Stop();
    }

    public void ShowStatus(string message, bool error = false) => Notify(message, error);

    public void RestoreFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = _lastVisibleState;
        Activate();
    }

    private void RefreshUi()
    {
        if (_shuttingDown) return;
        try
        {
            _service.Refresh();
            var advanced = _service.Settings.AdvancedLogging;
            DetailsTabs.Tag = advanced ? "True" : "False";
            PeersTab.Visibility = TrackersTab.Visibility = JournalTab.Visibility = advanced ? Visibility.Visible : Visibility.Collapsed;
            if (!advanced) DetailsTabs.SelectedIndex = 0;
            var selected = SelectedTorrent;
            // Avoid resetting selection, templates and layout every second.

            if (selected is not null && _view.Contains(selected)) TorrentList.SelectedItem = selected;
            else if (TorrentList.SelectedItem is null && !_view.IsEmpty) TorrentList.SelectedIndex = 0;
            DownloadRate.Text = DisplayFormat.SpeedNumber(_service.DownloadSpeed);
            UploadRate.Text = DisplayFormat.SpeedNumber(_service.UploadSpeed);
            DownloadUnit.Text = UploadUnit.Text = L.T("МБ/с");
            PortableLabel.ToolTip = L.T("Настройки, список раздач и журнал: ") + _service.DataDirectory;
            UpdateEmptyState();
            UpdateSelection();
            if (DateTime.UtcNow > _noticeUntil)
            {
                StatusText.Text = _busy ? L.T("Выполняется операция…") : _service.Torrents.Count == 0 ? L.T("Готов к загрузке · добавьте magnet-ссылку или .torrent-файл") : L.F($"Раздач: {_service.Torrents.Count}");
                StatusText.ToolTip = null;
                StatusDot.Fill = (Brush)FindResource("AccentBrush");
            }
        }
        catch (Exception ex) { Notify(L.T("Не удалось обновить состояние: ") + ex.Message, true); }
    }

    private void UpdateEmptyState()
    {
        var empty = _view.IsEmpty;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyTitle.Text = L.T("Начните с первого торрента");
        EmptyDescription.Text = L.T("Перетащите сюда .torrent-файл или добавьте magnet-ссылку.");
        EmptyAddButton.Visibility = Visibility.Visible;
    }

    private void UpdateSelection()
    {
        var item = SelectedTorrent;
        DetailsPanel.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        if (!ReferenceEquals(DetailsPanel.DataContext, item)) DetailsPanel.DataContext = item;
        PauseButton.IsEnabled = item is not null && !_busy;
        RemoveButton.IsEnabled = item is not null && !_busy;
        FolderButton.IsEnabled = item is not null;
        RetryButton.IsEnabled = item is not null && !_busy;
        PauseButton.Content = item?.IsPaused == true ? L.T("▶  Продолжить") : L.T("Ⅱ  Пауза");
        DownloadOrderText.Text = _service.Settings.SequentialByDefault ? L.T("Последовательный") : L.T("Обычный");
        RemainingTime.Text = item?.EtaSeconds is { } seconds ? DisplayFormat.Clock(seconds) : item?.EtaText ?? "—";
        RemainingTimeUnit.Text = item?.EtaSeconds is not null ? L.T("ч:мин:с") : "";
        var size = DisplayFormat.SizeParts(item?.Size ?? 0);
        SelectedSize.Text = item?.HasMetadata == true ? size.Number : "—";
        SelectedSizeUnit.Text = item?.HasMetadata == true ? size.Unit : "";
        SelectedSizeTorrent.Text = item?.Name ?? L.T("Выберите раздачу");
        SelectedSizeTorrent.ToolTip = item?.Name;
        RemainingTorrent.Text = item?.Name ?? L.T("Выберите раздачу");
        RemainingTorrent.ToolTip = item?.Name;
        RemainingTime.ToolTip = L.T("Оценка по скорости получения данных; меняется вместе с выбранной раздачей.");
        if (!_layoutLoaded && _service.IsInitialized)
        { _preferredDetailsHeight = _service.Settings.DetailsPaneHeight; _layoutLoaded = true; }
        _detailsVisible = item is not null;
        DetailsSplitter.Visibility = _detailsVisible ? Visibility.Visible : Visibility.Collapsed;
        ApplyDetailsHeight();
    }

    private void ContentGrid_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyDetailsHeight();

    private void ApplyDetailsHeight()
    {
        if (DetailsRow is null || ContentGrid.ActualHeight <= 0 || DetailsSplitter.IsDragging) return;
        var available = ContentGrid.ActualHeight - ContentGrid.RowDefinitions.Take(4).Sum(r => r.ActualHeight) - 136;
        DetailsRow.MinHeight = _detailsVisible ? 240 : 0;
        DetailsRow.Height = new GridLength(_detailsVisible ? Math.Clamp(_preferredDetailsHeight, 240, Math.Max(240, available)) : 0);
        SplitterRow.Height = new GridLength(_detailsVisible ? 10 : 0);
    }

    private async void DetailsSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        _preferredDetailsHeight = DetailsRow.ActualHeight;
        ContentGrid.RowDefinitions[4].Height = new GridLength(1, GridUnitType.Star);
        await PersistLayoutAsync();
        try { await _service.SaveDetailsHeightAsync(_preferredDetailsHeight); }
        catch (Exception ex) { Notify(L.T("Не удалось сохранить высоту панели: ") + ex.Message, true); }
    }

    private void TorrentList_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_view is not null) UpdateSelection(); }

    private async void Add_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var dialog = new AddTorrentWindow { Owner = this };
        if (dialog.ShowDialog() == true) await AddSourcesAsync(dialog.Sources);
    }

    private async void OpenTorrent_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = L.T("Добавить торренты"), Filter = L.T("Торрент-файлы (*.torrent)|*.torrent"), Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) await AddSourcesAsync(dialog.FileNames);
    }

    public async Task AddSourcesAsync(IEnumerable<string> sources)
    {
        if (_busy) { Notify(L.T("Дождитесь добавления текущего торрента.")); return; }
        _busy = true;
        AddButton.IsEnabled = false;
        try
        {
            foreach (var source in sources)
            {
                try
                {
                    var item = await _service.AddAsync(source.Trim());
                    _view.Refresh();
                    TorrentList.SelectedItem = item;
                    TorrentList.ScrollIntoView(item);
                    Notify(L.T("Добавлен: ") + item.Name);
                }
                catch (Exception ex) { ShowError(L.T("Не удалось добавить торрент"), ex); }
            }
        }
        finally { _busy = false; AddButton.IsEnabled = true; RefreshUi(); }
    }

    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || SelectedTorrent is not { } item) return;
        _busy = true;
        UpdateSelection();
        try { if (item.IsPaused) await _service.ResumeAsync(item); else await _service.PauseAsync(item); RefreshUi(); }
        catch (Exception ex) { ShowError(L.T("Не удалось изменить состояние"), ex); }
        finally { _busy = false; RefreshUi(); }
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || SelectedTorrent is not { } item) return;
        _busy = true;
        var choice = Dialogs.ChooseRemoval(this, item.Name);
        if (choice is null) { _busy = false; return; }
        RemoveButton.Content = L.T("Удаляется…");
        UpdateSelection();
        Notify(L.T("Закрываем раздачу и сохраняем данные…"));
        try { await _service.RemoveAsync(item, choice.Value); Notify(choice.Value ? L.T("Раздача удалена. Удаление файлов завершено.") : L.T("Торрент убран из библиотеки. Загруженные файлы сохранены.")); RefreshUi(); }
        catch (Exception ex) { ShowError(L.T("Не удалось убрать торрент"), ex); }
        finally { _busy = false; RemoveButton.Content = L.T("Удалить"); RefreshUi(); }
    }

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AboutWindow { Owner = this };
        dialog.ShowDialog();
        if (dialog.RemovalRequested && Application.Current is App app) await app.RemoveApplicationAsync();
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
        => await ShowSettingsAsync();

    public async Task ShowSettingsAsync(Dictionary<string, string>? draft = null, UpdateProgress? updateResult = null)
    {
        if (_busy) return;
        var app = Application.Current as App;
        var dialog = new SettingsWindow(_service.Settings, _service.DataDirectory, app?.Updates,
            app is null ? null : window => app.CheckForUpdatesAsync(true, window)) { Owner = this };
        app?.AttachUpdateDialog(dialog);
        if (draft is not null) dialog.RestoreDraft(draft);
        if (updateResult is not null) dialog.ShowUpdateProgress(updateResult);
        var result = dialog.ShowDialog();
        if (app is not null)
        {
            if (dialog.RemovalRequested) { await app.RemoveApplicationAsync(); return; }
        }
        if (result != true || dialog.Result is null) return;
        _busy = true; UpdateSelection();
        try
        {
            await _service.ApplySettingsAsync(dialog.Result);
            L.SetLanguage(_service.Settings.Language);
            UiScale.SetFactor(_service.Settings.UiScale);
            Language = System.Windows.Markup.XmlLanguage.GetLanguage(L.Culture.IetfLanguageTag);
            TorrentList.Items.Refresh();
            DetailsPanel.DataContext = null;
            Notify(L.T("Настройки сохранены.")); RefreshUi();
        }
        catch (Exception ex) { ShowError(L.T("Не удалось применить настройки"), ex); }
        finally { _busy = false; RefreshUi(); }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTorrent is not { } item || _busy) return;
        _busy = true;
        UpdateSelection();
        try { await _service.RetryAsync(item); Notify(L.T("Запрос соединений отправлен.")); }
        catch (Exception ex) { ShowError(L.T("Не удалось обновить соединения"), ex); }
        finally { _busy = false; RefreshUi(); }
    }

    private void CopyDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTorrent is not { } item) return;
        try
        {
            var lines = new List<string>
            {
                "SneakyFlow · " + DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"),
                item.Name, L.T("Статус: ") + item.Status, L.T("Диагностика: ") + item.Diagnosis,
                L.F($"Загружено: {DisplayFormat.Bytes(item.Downloaded)} / {DisplayFormat.Bytes(item.Size)} ({item.Progress:0.00}%)"),
                L.F($"Скорость: ↓ {DisplayFormat.Speed(item.DownloadSpeed)}, ↑ {DisplayFormat.Speed(item.UploadSpeed)}"),
                L.F($"Сиды: подключено {item.ConnectedSeeds}, у трекеров {item.TrackerSeeds?.ToString() ?? L.T("неизвестно")}"),
                L.F($"Пиры: подключено {item.ConnectedLeechers}, у трекеров {item.TrackerLeechers?.ToString() ?? L.T("неизвестно")}"),
                item.ConnectionSummary, item.NetworkStatus, item.DhtStatus, item.DiskStatus, item.TransferStatus,
                L.F($"Неудачных подключений за сеанс: {item.FailedConnectionAttempts}"),
                L.F($"Сегменты: {item.CompletedPieces} / {item.PieceCount}; запросов блоков: {item.RequestedBlocks}; ошибок хеша: {item.HashFailures}"),
                L.T("Последние данные: ") + item.LastActivity,
                L.T("Трекеры: ") + item.TrackerSummary
            };
            // Exclude full tracker URLs, save paths and infohashes from the shareable report.
            foreach (var tracker in item.Trackers)
            {
                var host = Uri.TryCreate(tracker.Address, UriKind.Absolute, out var address) ? address.Host : L.T("Трекер");
                lines.Add($"{host}: {tracker.Status} · {tracker.Message}");
            }
            Clipboard.SetText(string.Join(Environment.NewLine, lines));
            Notify(L.T("Отчёт скопирован в буфер обмена."));
        }
        catch (Exception ex) { ShowError(L.T("Не удалось скопировать отчёт"), ex); }
    }

    private void Folder_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTorrent is not { } item) return;
        try
        {
            var folder = Directory.Exists(item.SavePath) ? item.SavePath : Path.GetDirectoryName(item.SavePath);
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) throw new DirectoryNotFoundException(L.T("Папка появится после начала загрузки."));
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError(L.T("Не удалось открыть папку"), ex); }
    }

    private void ProfileFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_service.DataDirectory);
            Process.Start(new ProcessStartInfo(_service.DataDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowError(L.T("Не удалось открыть папку данных"), ex); }
    }

    private static string[] GetDroppedSources(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] paths)
            return paths.Where(x => Path.GetExtension(x).Equals(".torrent", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text && text.Trim().StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) return [text.Trim()];
        return [];
    }

    private void Window_DragOver(object sender, DragEventArgs e) { e.Effects = GetDroppedSources(e.Data).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void Window_Drop(object sender, DragEventArgs e) { e.Handled = true; var sources = GetDroppedSources(e.Data); if (sources.Length > 0) await AddSourcesAsync(sources); }
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.L) { Add_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O) { OpenTorrent_Click(this, new RoutedEventArgs()); e.Handled = true; }
    }

    private void Service_Warning(object? sender, string message) => Dispatcher.BeginInvoke(() => Notify(message, true));
    private void Notify(string message, bool error = false) { StatusText.Text = message; StatusDot.Fill = (Brush)FindResource(error ? "DangerBrush" : "AccentBrush"); _noticeUntil = DateTime.UtcNow.AddSeconds(error ? 30 : 12); StatusText.ToolTip = message; }
    private void ShowError(string title, Exception ex) { Notify(title + ": " + ex.Message, true); Dialogs.ShowMessage(this, title, ex.Message); }
    protected override void OnClosed(EventArgs e) { _refreshTimer.Stop(); _layoutTimer.Stop(); _service.Warning -= Service_Warning; base.OnClosed(e); }
}
