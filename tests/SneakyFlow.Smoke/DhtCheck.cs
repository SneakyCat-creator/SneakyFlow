using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using MonoTorrent;
using MonoTorrent.Dht;
using SneakyFlow.Models;
using SneakyFlow.Services;

internal static class DhtCheck
{
    // Opt-in public DHT bootstrap test. Never uses the user's torrents or download paths.
    public static async Task RunAsync()
    {
        var root = Path.GetFullPath(Path.Combine("artifacts", "dht-check", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "synthetic.bin");
        await File.WriteAllBytesAsync(path, new byte[65536]);
        var metadata = await new TorrentCreator(TorrentType.V1Only).CreateAsync(new TorrentFileSource(path));
        var source = Path.Combine(root, "synthetic.torrent");
        await File.WriteAllBytesAsync(source, metadata.Encode());
        var profile = Path.Combine(root, "profile");
        var data = Directory.CreateDirectory(Path.Combine(profile, "SneakyFlow.Data")).FullName;
        await File.WriteAllTextAsync(Path.Combine(data, "session.json"), JsonSerializer.Serialize(new
        {
            Settings = new AppSettings { DownloadDirectory = Path.Combine(root, "download"), EnableDht = true,
                EnableLocalPeerDiscovery = false, EnablePortForwarding = false, SequentialByDefault = false }
        }));
        await using var service = new TorrentService(profile);
        await service.InitializeAsync();
        await service.AddAsync(source);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var dht = (IDhtEngine)typeof(TorrentService).GetField("_dht", flags)!.GetValue(service)!;
        // Reproduce the observed stuck NotReady state, then exercise service recovery.
        await dht.StopAsync();
        typeof(TorrentService).GetField("_lastDhtRecovery", flags)!.SetValue(service, DateTimeOffset.MinValue);
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(50))
        {
            service.Refresh();
            Console.WriteLine($"DHT recovery {watch.Elapsed.TotalSeconds:F1}s: {dht.State}, nodes={dht.NodeCount}");
            if (dht.State == DhtState.Ready && dht.NodeCount > 0)
            {
                Console.WriteLine("PASS actual DHT recovery after NotReady; " + root);
                return;
            }
            await Task.Delay(2000);
        }
        throw new TimeoutException("Public DHT did not become ready in 50s; inspect " + data);
    }
}
