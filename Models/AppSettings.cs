namespace SneakyFlow.Models;

public sealed record AppSettings
{
    public double UiScale { get; set; } = 1;
    public string Language { get; set; } = "ru";
    public string DownloadDirectory { get; set; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "SneakyFlow");
    public int MaxDownloadKiB { get; set; }
    public int MaxUploadKiB { get; set; }
    public int MaxConcurrentDownloads { get; set; } = 3;
    public int MaxConnections { get; set; } = 150;
    public int ListenPort { get; set; }
    public bool EnableDht { get; set; } = true;
    public bool EnableLocalPeerDiscovery { get; set; } = true;
    public bool EnablePortForwarding { get; set; } = true;
    public bool StartAddedTorrents { get; set; } = true;
    public bool SequentialByDefault { get; set; }
    public bool AdvancedLogging { get; set; }
    public bool AutomaticUpdates { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool MinimizeToTray { get; set; }
    public double DetailsPaneHeight { get; set; } = 340;
}
