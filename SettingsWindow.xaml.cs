using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SneakyFlow.Models;
using SneakyFlow.Services;
using SneakyFlow.UI;
using System.ComponentModel;
using System.Windows.Media;

namespace SneakyFlow;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _original;
    private readonly string _dataDirectory;
    public AppSettings? Result { get; private set; }
    public bool RemovalRequested { get; private set; }
    private readonly UpdateActivity? _updates;
    private readonly Func<SettingsWindow, Task>? _checkUpdates;
    private readonly CancellationTokenSource _updateCancellation = new();
    private bool _closed, _allowRestart;
    internal UpdateButtonState UpdateState { get; } = new();
    public CancellationToken UpdateCancellation => _updateCancellation.Token;

    public SettingsWindow(AppSettings settings, string dataDirectory, UpdateActivity? updates = null, Func<SettingsWindow, Task>? checkUpdates = null)
    {
        InitializeComponent();
        _original = settings;
        _dataDirectory = dataDirectory;
        _updates = updates; _checkUpdates = checkUpdates;
        UpdateAction.State = UpdateState;
        if (_updates is not null)
        {
            _updates.Changed += ShowUpdateProgress;
            // A finished check belongs to that particular opening of Settings.
            if (_updates.Current.IsActive) ShowUpdateProgress(_updates.Current);
        }
        LanguageBox.SelectedValue = settings.Language;
        ScaleBox.SelectedValue = settings.UiScale.ToString(CultureInfo.InvariantCulture);
        if (ScaleBox.SelectedIndex < 0) ScaleBox.SelectedIndex = 0;
        DownloadDirectoryBox.Text = settings.DownloadDirectory;
        MaxDownloadBox.Text = UI.RateLimitInput.Format(settings.MaxDownloadKiB);
        MaxUploadBox.Text = UI.RateLimitInput.Format(settings.MaxUploadKiB);
        MaxConcurrentBox.Text = settings.MaxConcurrentDownloads.ToString(CultureInfo.InvariantCulture);
        MaxConnectionsBox.Text = settings.MaxConnections.ToString(CultureInfo.InvariantCulture);
        ListenPortBox.Text = settings.ListenPort.ToString(CultureInfo.InvariantCulture);
        StartAddedBox.IsChecked = settings.StartAddedTorrents;
        SequentialDefaultBox.IsChecked = settings.SequentialByDefault;
        EnableDhtBox.IsChecked = settings.EnableDht;
        EnableLocalPeersBox.IsChecked = settings.EnableLocalPeerDiscovery;
        EnablePortForwardingBox.IsChecked = settings.EnablePortForwarding;
        CloseToTrayBox.IsChecked = settings.CloseToTray;
        MinimizeToTrayBox.IsChecked = settings.MinimizeToTray;
        AdvancedLoggingBox.IsChecked = settings.AdvancedLogging;
        AutomaticUpdatesBox.IsChecked = settings.AutomaticUpdates;
        DataDirectoryLabel.Text = dataDirectory;
    }

    public void ShowUpdateProgress(UpdateProgress progress)
    {
        if (_closed) return;
        UpdateState.Apply(progress);
        bool detailChanged = UpdateDetail.Text != (progress.Detail ?? "");
        UpdateDetail.Text = progress.Detail ?? "";
        UpdateDetail.Foreground = (Brush)FindResource(progress.Stage == UpdateStage.Failed ? "DangerBrush" : "MutedBrush");
        SettingsFields.IsEnabled = SaveButton.IsEnabled = CancelButton.IsEnabled = RemoveAppButton.IsEnabled = progress.Stage != UpdateStage.Installing;
        if (detailChanged && !string.IsNullOrWhiteSpace(progress.Detail))
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(() => { if (!_closed) UpdatePanel.BringIntoView(); }));
    }

    public void AllowUpdateRestart() => _allowRestart = true;
    protected override void OnClosing(CancelEventArgs e)
    {
        // Once handoff begins, replacing the EXE must not race a second settings/uninstall action.
        if (UpdateState.IsInstalling && !_allowRestart) e.Cancel = true;
        base.OnClosing(e);
    }
    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        if (_updates is not null) _updates.Changed -= ShowUpdateProgress;
        if (!_allowRestart) _updateCancellation.Cancel();
        base.OnClosed(e);
    }
    private static readonly string[] DraftFields = [nameof(DownloadDirectoryBox), nameof(MaxDownloadBox), nameof(MaxUploadBox),
        nameof(MaxConcurrentBox), nameof(MaxConnectionsBox), nameof(ListenPortBox), nameof(LanguageBox), nameof(ScaleBox),
        nameof(StartAddedBox), nameof(SequentialDefaultBox), nameof(EnableDhtBox), nameof(EnableLocalPeersBox),
        nameof(EnablePortForwardingBox), nameof(CloseToTrayBox), nameof(MinimizeToTrayBox), nameof(AdvancedLoggingBox), nameof(AutomaticUpdatesBox)];
    public Dictionary<string, string> CaptureDraft() => DraftFields.ToDictionary(name => name, name => FindName(name) switch
    {
        TextBox box => box.Text,
        ComboBox box => box.SelectedValue?.ToString() ?? "",
        CheckBox box => (box.IsChecked == true).ToString(),
        _ => ""
    });
    public void RestoreDraft(IReadOnlyDictionary<string, string> draft)
    {
        foreach (var name in DraftFields)
            if (draft.TryGetValue(name, out var value))
                switch (FindName(name))
                {
                    case TextBox box: box.Text = value; break;
                    case ComboBox box: box.SelectedValue = value; break;
                    case CheckBox box when bool.TryParse(value, out var enabled): box.IsChecked = enabled; break;
                }
        Loaded += (_, _) => UpdateAction.BringIntoView();
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = L.T("Папка для загруженных раздач"), Multiselect = false };
        if (Directory.Exists(DownloadDirectoryBox.Text.Trim())) dialog.InitialDirectory = DownloadDirectoryBox.Text.Trim();
        if (dialog.ShowDialog(this) == true) { DownloadDirectoryBox.Text = dialog.FolderName; ErrorText.Text = ""; }
    }

    private void ProfileFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_dataDirectory);
            Process.Start(new ProcessStartInfo(_dataDirectory) { UseShellExecute = true });
        }
        catch (Exception error) { ErrorText.Text = L.T("Не удалось открыть папку данных") + ": " + error.Message; }
    }

    private static int ReadInt(TextBox field, string label, int min, int max)
    {
        if (!int.TryParse(field.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < min || value > max)
        {
            field.Focus();
            field.SelectAll();
            throw new FormatException(L.F($"{label}: введите число от {min} до {max}."));
        }
        return value;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(DownloadDirectoryBox.Text)) throw new FormatException(L.T("Укажите папку загрузок."));
            Result = _original with
            {
                UiScale = double.Parse((string)ScaleBox.SelectedValue, CultureInfo.InvariantCulture),
                Language = LanguageBox.SelectedValue as string ?? "ru",
                DownloadDirectory = Path.GetFullPath(DownloadDirectoryBox.Text.Trim()),
                MaxDownloadKiB = UI.RateLimitInput.Parse(MaxDownloadBox.Text, L.T("Скорость скачивания")),
                MaxUploadKiB = UI.RateLimitInput.Parse(MaxUploadBox.Text, L.T("Скорость отдачи")),
                MaxConcurrentDownloads = ReadInt(MaxConcurrentBox, L.T("Одновременные загрузки"), 1, 100),
                MaxConnections = ReadInt(MaxConnectionsBox, L.T("Соединения"), 1, 5000),
                ListenPort = ReadInt(ListenPortBox, L.T("Порт"), 0, 65535),
                StartAddedTorrents = StartAddedBox.IsChecked == true,
                SequentialByDefault = SequentialDefaultBox.IsChecked == true,
                EnableDht = EnableDhtBox.IsChecked == true,
                EnableLocalPeerDiscovery = EnableLocalPeersBox.IsChecked == true,
                EnablePortForwarding = EnablePortForwardingBox.IsChecked == true,
                CloseToTray = CloseToTrayBox.IsChecked == true,
                MinimizeToTray = MinimizeToTrayBox.IsChecked == true,
                AdvancedLogging = AdvancedLoggingBox.IsChecked == true,
                AutomaticUpdates = AutomaticUpdatesBox.IsChecked == true
            };
            DialogResult = true;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IOException or NotSupportedException)
        {
            Result = null;
            ErrorText.Text = ex.Message;
        }
    }

    private void RemoveApp_Click(object sender, RoutedEventArgs e)
    {
        if (!UI.Dialogs.Confirm(this, L.T("САМОУНИЧТОЖЕНИЕ"), L.T("Программа, её известные запущенные копии и данные профиля будут удалены навсегда, без корзины. Скачанные файлы вне профиля останутся. Продолжить?"))) return;
        RemovalRequested = true; Close();
    }
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (!UpdateState.CanClick) return;
        ShowUpdateProgress(new(UpdateStage.Checking));
        try
        {
            if (_checkUpdates is null) throw new InvalidOperationException(L.T("Проверка обновлений недоступна."));
            await _checkUpdates(this);
        }
        catch (OperationCanceledException) { ShowUpdateProgress(new(UpdateStage.Cancelled)); }
        catch (Exception error) { ShowUpdateProgress(new(UpdateStage.Failed, Detail: error.Message)); }
    }
}
