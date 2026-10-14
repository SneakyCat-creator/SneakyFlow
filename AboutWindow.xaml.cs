using System.Diagnostics;
using System.IO;
using System.Windows;
using SneakyFlow.UI;
using SneakyFlow.Services;

namespace SneakyFlow;

public partial class AboutWindow : Window
{
    private bool _loaded;
    public bool Accepted { get; private set; }
    public bool RemovalRequested { get; private set; }
    public AboutWindow(bool requireAcceptance = false)
    {
        InitializeComponent();
        VersionText.Text = L.F($"Версия {AppVersion.Display} · Windows x64");
        LicenseVersionText.Text = L.T("Версия соглашения: ") + LicensePolicy.Version;
        AcceptButton.Visibility = DeclineButton.Visibility = requireAcceptance ? Visibility.Visible : Visibility.Collapsed;
        AcceptedBox.Visibility = CloseButton.Visibility = requireAcceptance ? Visibility.Collapsed : Visibility.Visible;
        AcceptedBox.IsChecked = !requireAcceptance;
        _loaded = true;
    }

    private void Accept_Click(object sender, RoutedEventArgs e) { Accepted = true; Close(); }
    private void Decline_Click(object sender, RoutedEventArgs e) { RemovalRequested = true; Close(); }
    private void Accepted_Unchecked(object sender, RoutedEventArgs e)
    { if (_loaded) { RemovalRequested = true; Close(); } }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Licenses_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            // These notices travel inside the single EXE and are extracted by .NET.
            var path = Path.Combine(AppContext.BaseDirectory, "licenses");
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception error) { Dialogs.ShowMessage(this, L.T("О программе"), error.Message); }
    }
}
