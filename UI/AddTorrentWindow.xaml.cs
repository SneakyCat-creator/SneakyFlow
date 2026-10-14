using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace SneakyFlow.UI;

public partial class AddTorrentWindow : Window
{
    public string[] Sources { get; private set; } = [];
    public AddTorrentWindow() { InitializeComponent(); Loaded += (_, _) => SourceBox.Focus(); }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = L.T("Выбрать торрент-файлы"), Filter = L.T("Торрент-файлы (*.torrent)|*.torrent"), Multiselect = true, CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) SourceBox.Text = string.Join(Environment.NewLine, dialog.FileNames);
    }
    private void Source_TextChanged(object sender, TextChangedEventArgs e) { if (ErrorText is not null) ErrorText.Text = ""; }
    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var sources = SourceBox.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => x.Trim('"')).ToArray();
        if (sources.Length == 0) { ErrorText.Text = L.T("Вставьте magnet-ссылку или выберите .torrent-файл."); return; }
        foreach (var source in sources)
        {
            if (source.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase)) continue;
            if (Path.GetExtension(source).Equals(".torrent", StringComparison.OrdinalIgnoreCase) && File.Exists(source)) continue;
            ErrorText.Text = L.T("Нужна magnet-ссылка или существующий файл с расширением .torrent."); return;
        }
        Sources = sources;
        DialogResult = true;
    }
}
